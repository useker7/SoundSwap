using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.CoreAudioApi;

namespace SoundSwap;

/// <summary>
/// UDP server. Every phone that keeps sending HELLO is a connected client (up to <see cref="MaxClients"/>):
/// PC audio is sent to all of them, and their microphones are mixed into the virtual microphone.
/// The audio source and microphone output can be changed while running.
/// </summary>
sealed class AudioServer : IDisposable
{
    public const int MaxClients = 8;

    /// <summary>Snapshot of a connected phone for the UI.</summary>
    public sealed record ClientInfo(string Name, IPAddress Address, bool WantsPcAudio, bool SendsMic, bool ViaUsb);

    sealed class Client(IPEndPoint endPoint, string name, byte flags, bool viaUsb)
    {
        public readonly IPEndPoint EndPoint = endPoint;
        public readonly bool ViaUsb = viaUsb;
        public volatile string Name = name;
        public volatile byte Flags = flags;
        public long LastSeen = Environment.TickCount64;
        public uint LastMicSeq;
        public bool HasMicSeq;
        // The mic channel belongs to one MicOutput; it is reopened if the output device changes.
        public MicOutput? MicOwner;
        public MicMixer.Channel? MicChannel;

        public bool WantsPcAudio => (Flags & Protocol.FlagWantsPcAudio) != 0;
        public bool SendsMic => (Flags & Protocol.FlagSendsMic) != 0;
    }

    static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(3);

    readonly Socket _socket;
    readonly Thread _recvThread;
    readonly byte[] _audioPacket = new byte[Protocol.HeaderSize + Protocol.FrameSamples * 2 * 2];
    readonly byte[] _nameBytes = Encoding.UTF8.GetBytes(Environment.MachineName);
    readonly object _configLock = new();
    readonly ConcurrentDictionary<IPEndPoint, Client> _clients = new();

    volatile LoopbackSource? _loopback;
    volatile MicOutput? _mic;
    volatile bool _running = true;
    uint _seq;

    public long PacketsSent;
    public long PacketsReceived;
    public long MicPacketsLost;

    public AudioServer()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows: keep ReceiveFrom from throwing on the ICMP "port unreachable" sent when the peer goes away.
        _socket.IOControl(unchecked((int)0x9800000C), new byte[4], null);
        _socket.EnableBroadcast = true;
        _socket.ReceiveTimeout = 500;
        _socket.ReceiveBufferSize = 512 * 1024;
        _socket.SendBufferSize = 512 * 1024;
        try { _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8); } catch { }
        _socket.Bind(new IPEndPoint(IPAddress.Any, Protocol.Port));

        _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "udp-recv" };
        _recvThread.Start();
    }

    public IReadOnlyList<ClientInfo> Clients =>
        _clients.Values
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ClientInfo(c.Name, c.EndPoint.Address, c.WantsPcAudio, c.SendsMic, c.ViaUsb))
            .ToList();

    public bool IsConnected => !_clients.IsEmpty;
    public bool AnyClientWantsPcAudio => _clients.Values.Any(c => c.WantsPcAudio);
    public bool AnyClientSendsMic => _clients.Values.Any(c => c.SendsMic);

    public float SendPeak => _loopback?.Peak ?? 0;
    public float MicPeak => _mic?.Mixer.Peak ?? 0;
    public double MicBufferedMs => _mic?.Mixer.BufferedMs ?? 0;
    public long MicUnderruns => _mic?.Mixer.Underruns ?? 0;
    public string? SourceError => _loopback?.Error;

    public int MicBufferMs { set { var m = _mic; if (m != null) m.Mixer.TargetMs = value; } }

    /// <summary>Output device captured and sent to the phones; null = don't send.</summary>
    public void SetSource(string? deviceId)
    {
        lock (_configLock)
        {
            var old = _loopback;
            _loopback = null;
            old?.Dispose();
            if (deviceId != null)
            {
                var lb = new LoopbackSource(deviceId, OnLoopbackFrame);
                lb.Start();
                _loopback = lb;
            }
        }
    }

    /// <summary>Device the phone microphones are played into (feeds the virtual microphone); null = unused.</summary>
    public void SetMicOutput(string? deviceId, int bufferMs)
    {
        lock (_configLock)
        {
            var old = _mic;
            _mic = null;
            old?.Dispose();
            if (deviceId != null)
            {
                using var enumerator = new MMDeviceEnumerator();
                _mic = new MicOutput(enumerator.GetDevice(deviceId), bufferMs);
            }
        }
    }

    void OnLoopbackFrame(ReadOnlySpan<float> stereo)
    {
        if (!AnyClientWantsPcAudio) return;

        var pkt = _audioPacket;
        Protocol.WriteHeader(pkt, Protocol.PacketType.PcAudio, _seq++);
        int o = Protocol.HeaderSize;
        foreach (float f in stereo)
        {
            short s = (short)Math.Clamp((int)MathF.Round(f * 32767f), short.MinValue, short.MaxValue);
            pkt[o++] = (byte)s;
            pkt[o++] = (byte)(s >> 8);
        }
        // The same packet goes to every listening phone; each sees one continuous sequence.
        foreach (var c in _clients.Values)
        {
            if (!c.WantsPcAudio) continue;
            try
            {
                _socket.SendTo(pkt, 0, o, SocketFlags.None, c.EndPoint);
                PacketsSent++;
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    void ReceiveLoop()
    {
        var buf = new byte[4096];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            int n;
            try
            {
                n = _socket.ReceiveFrom(buf, ref from);
            }
            catch (SocketException)
            {
                CheckTimeouts();
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var span = buf.AsSpan(0, n);
            if (!Protocol.TryParse(span, out var type, out uint seq)) continue;
            var ep = (IPEndPoint)from;
            var payload = span[Protocol.HeaderSize..];

            switch (type)
            {
                case Protocol.PacketType.Discover:
                    Send(Protocol.PacketType.DiscoverReply, _nameBytes, ep);
                    break;

                case Protocol.PacketType.Hello:
                {
                    byte flags = payload.Length > 0 ? payload[0] : (byte)0;
                    string name = payload.Length > 1 ? Encoding.UTF8.GetString(payload[1..]) : "Phone";
                    if (_clients.TryGetValue(ep, out var c))
                    {
                        c.Flags = flags;
                        c.Name = name;
                        c.LastSeen = Environment.TickCount64;
                    }
                    else
                    {
                        if (_clients.Count >= MaxClients) break; // full: no ACK, the phone keeps waiting
                        var key = new IPEndPoint(ep.Address, ep.Port);
                        _clients[key] = new Client(key, name, flags, LinkInfo.IsViaUsb(ep.Address));
                    }
                    Send(Protocol.PacketType.HelloAck, _nameBytes, ep);
                    break;
                }

                case Protocol.PacketType.MicAudio:
                {
                    if (!_clients.TryGetValue(ep, out var c)) break;
                    c.LastSeen = Environment.TickCount64;
                    if (c.HasMicSeq)
                    {
                        int diff = (int)(seq - c.LastMicSeq);
                        if (diff <= 0 && diff > -1000) break; // late / duplicate packet
                        if (diff > 1) MicPacketsLost += diff - 1;
                    }
                    c.LastMicSeq = seq;
                    c.HasMicSeq = true;
                    PacketsReceived++;

                    var mic = _mic;
                    if (mic == null) break;
                    if (c.MicOwner != mic)
                    {
                        c.MicOwner = mic;
                        c.MicChannel = mic.Mixer.Open();
                    }
                    c.MicChannel!.Push(payload);
                    break;
                }

                case Protocol.PacketType.Bye:
                    Remove(ep);
                    break;
            }
            CheckTimeouts();
        }
    }

    void CheckTimeouts()
    {
        long now = Environment.TickCount64;
        foreach (var c in _clients.Values)
            if (now - c.LastSeen > ClientTimeout.TotalMilliseconds)
                Remove(c.EndPoint);
    }

    void Remove(IPEndPoint ep)
    {
        if (!_clients.TryRemove(ep, out var c)) return;
        if (c.MicChannel != null && c.MicOwner == _mic) c.MicOwner!.Mixer.Close(c.MicChannel);
    }

    void Send(Protocol.PacketType type, byte[] payload, EndPoint to)
    {
        var pkt = new byte[Protocol.HeaderSize + payload.Length];
        Protocol.WriteHeader(pkt, type, 0);
        payload.CopyTo(pkt, Protocol.HeaderSize);
        try { _socket.SendTo(pkt, to); } catch (SocketException) { }
    }

    public void Dispose()
    {
        _running = false;
        _clients.Clear();
        SetSource(null);
        _socket.Dispose();
        _recvThread.Join(1000);
        SetMicOutput(null, 0);
    }
}
