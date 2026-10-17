using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.CoreAudioApi;

namespace SoundSwap;

/// <summary>
/// UDP sunucusu. Telefon HELLO gönderdikçe onu "bağlı istemci" kabul eder, PC sesini ona yollar
/// ve ondan gelen mikrofon paketlerini sanal mikrofona çalar. Tek seferde bir telefon.
/// Ses kaynağı ve mikrofon çıkışı çalışırken değiştirilebilir.
/// </summary>
sealed class AudioServer : IDisposable
{
    sealed class Client(IPEndPoint endPoint, string name, byte flags)
    {
        public IPEndPoint EndPoint = endPoint;
        public string Name = name;
        public byte Flags = flags;
        public long LastSeen = Environment.TickCount64;
        public uint LastMicSeq;
        public bool HasMicSeq;
    }

    static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(3);

    readonly Socket _socket;
    readonly Thread _recvThread;
    readonly byte[] _audioPacket = new byte[Protocol.HeaderSize + Protocol.FrameSamples * 2 * 2];
    readonly byte[] _nameBytes = Encoding.UTF8.GetBytes(Environment.MachineName);
    readonly object _configLock = new();

    volatile LoopbackSource? _loopback;
    volatile MicOutput? _mic;
    volatile Client? _client;
    volatile bool _running = true;
    uint _seq;

    public long PacketsSent;
    public long PacketsReceived;
    public long MicPacketsLost;

    public AudioServer()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows: karşı taraf kapanınca gelen ICMP "port unreachable" yüzünden ReceiveFrom patlamasın.
        _socket.IOControl(unchecked((int)0x9800000C), new byte[4], null);
        _socket.EnableBroadcast = true;
        _socket.ReceiveTimeout = 500;
        _socket.ReceiveBufferSize = 256 * 1024;
        try { _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8); } catch { }
        _socket.Bind(new IPEndPoint(IPAddress.Any, Protocol.Port));

        _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "udp-recv" };
        _recvThread.Start();
    }

    public string? ClientName => _client?.Name;
    public IPAddress? ClientAddress => _client?.EndPoint.Address;
    public bool IsConnected => _client != null;
    public bool ClientWantsPcAudio => _client is { } c && (c.Flags & Protocol.FlagWantsPcAudio) != 0;
    public bool ClientSendsMic => _client is { } c && (c.Flags & Protocol.FlagSendsMic) != 0;

    public float SendPeak => _loopback?.Peak ?? 0;
    public float MicPeak => _mic?.Peak ?? 0;
    public double MicBufferedMs => _mic?.Jitter.BufferedMs ?? 0;
    public long MicUnderruns => _mic?.Jitter.Underruns ?? 0;
    public string? SourceError => _loopback?.Error;

    public int MicBufferMs { set { var m = _mic; if (m != null) m.TargetMs = value; } }

    /// <summary>Telefona gidecek sesin yakalanacağı çıkış cihazı; null = gönderme.</summary>
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

    /// <summary>Telefon mikrofonunun çalınacağı (sanal mikrofona giden) cihaz; null = kullanma.</summary>
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
        var c = _client;
        if (c == null || (c.Flags & Protocol.FlagWantsPcAudio) == 0) return;

        var pkt = _audioPacket;
        Protocol.WriteHeader(pkt, Protocol.PacketType.PcAudio, _seq++);
        int o = Protocol.HeaderSize;
        foreach (float f in stereo)
        {
            short s = (short)Math.Clamp((int)MathF.Round(f * 32767f), short.MinValue, short.MaxValue);
            pkt[o++] = (byte)s;
            pkt[o++] = (byte)(s >> 8);
        }
        try
        {
            _socket.SendTo(pkt, 0, o, SocketFlags.None, c.EndPoint);
            PacketsSent++;
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
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
                CheckTimeout();
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
                    string name = payload.Length > 1 ? Encoding.UTF8.GetString(payload[1..]) : "Telefon";
                    var c = _client;
                    if (c == null || !c.EndPoint.Equals(ep))
                    {
                        _client = new Client(new IPEndPoint(ep.Address, ep.Port), name, flags);
                        _mic?.Jitter.Clear();
                    }
                    else
                    {
                        c.Flags = flags;
                        c.Name = name;
                        c.LastSeen = Environment.TickCount64;
                    }
                    Send(Protocol.PacketType.HelloAck, _nameBytes, ep);
                    break;
                }

                case Protocol.PacketType.MicAudio:
                {
                    var c = _client;
                    if (c == null || !c.EndPoint.Equals(ep)) break;
                    c.LastSeen = Environment.TickCount64;
                    if (c.HasMicSeq)
                    {
                        int diff = (int)(seq - c.LastMicSeq);
                        if (diff <= 0 && diff > -1000) break; // geç / yinelenen paket
                        if (diff > 1) MicPacketsLost += diff - 1;
                    }
                    c.LastMicSeq = seq;
                    c.HasMicSeq = true;
                    PacketsReceived++;
                    _mic?.Push(payload);
                    break;
                }

                case Protocol.PacketType.Bye:
                    if (_client?.EndPoint.Equals(ep) == true) _client = null;
                    break;
            }
            CheckTimeout();
        }
    }

    void CheckTimeout()
    {
        var c = _client;
        if (c != null && Environment.TickCount64 - c.LastSeen > ClientTimeout.TotalMilliseconds)
            _client = null;
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
        _client = null;
        SetSource(null);
        _socket.Dispose();
        _recvThread.Join(1000);
        SetMicOutput(null, 0);
    }
}
