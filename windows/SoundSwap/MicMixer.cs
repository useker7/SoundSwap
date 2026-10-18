namespace SoundSwap;

/// <summary>
/// Mixes the microphones of several phones into one mono 48 kHz stream.
/// Each phone gets its own <see cref="Channel"/> with its own jitter buffer, so one phone's network
/// hiccups never stall the others. Has no audio-device dependencies so it can be unit tested.
/// </summary>
sealed class MicMixer
{
    public sealed class Channel
    {
        internal readonly JitterBuffer Jitter;
        float[] _floatBuf = new float[960];

        public volatile float Peak;

        internal Channel(int targetMs)
        {
            Jitter = new JitterBuffer(1, Protocol.SampleRate, 500, targetMs);
        }

        public double BufferedMs => Jitter.BufferedMs;
        public long Underruns => Jitter.Underruns;

        /// <summary>s16le mono PCM, as received from the network.</summary>
        public void Push(ReadOnlySpan<byte> pcm)
        {
            int n = pcm.Length / 2;
            if (_floatBuf.Length < n) _floatBuf = new float[n];
            float peak = 0;
            for (int i = 0; i < n; i++)
            {
                float s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f;
                _floatBuf[i] = s;
                peak = Math.Max(peak, Math.Abs(s));
            }
            Peak = Math.Max(peak, Peak * 0.9f);
            Jitter.Write(_floatBuf.AsSpan(0, n));
        }
    }

    readonly object _lock = new();
    volatile Channel[] _channels = [];
    float[] _scratch = new float[1024];
    int _targetMs;

    public MicMixer(int targetMs)
    {
        _targetMs = targetMs;
    }

    public int TargetMs
    {
        set
        {
            lock (_lock)
            {
                _targetMs = value;
                foreach (var c in _channels) c.Jitter.TargetMs = value;
            }
        }
    }

    public int ChannelCount => _channels.Length;
    public float Peak => _channels.Select(c => c.Peak).DefaultIfEmpty(0).Max();
    public long Underruns => _channels.Sum(c => c.Underruns);
    public double BufferedMs => _channels.Select(c => c.BufferedMs).DefaultIfEmpty(0).Max();

    public Channel Open()
    {
        lock (_lock)
        {
            var ch = new Channel(_targetMs);
            _channels = [.. _channels, ch]; // copy-on-write: the audio thread reads without locking
            return ch;
        }
    }

    public void Close(Channel channel)
    {
        lock (_lock) _channels = _channels.Where(c => c != channel).ToArray();
    }

    /// <summary>Fills <paramref name="dest"/> with the sum of all channels, clipped to [-1, 1].</summary>
    public void Mix(Span<float> dest)
    {
        var channels = _channels;
        if (channels.Length == 0)
        {
            dest.Clear();
            return;
        }
        channels[0].Jitter.Read(dest);
        if (channels.Length == 1) return;

        if (_scratch.Length < dest.Length) _scratch = new float[dest.Length];
        var tmp = _scratch.AsSpan(0, dest.Length);
        for (int c = 1; c < channels.Length; c++)
        {
            channels[c].Jitter.Read(tmp);
            for (int i = 0; i < dest.Length; i++) dest[i] += tmp[i];
        }
        for (int i = 0; i < dest.Length; i++) dest[i] = Math.Clamp(dest[i], -1f, 1f);
    }
}
