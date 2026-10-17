namespace SoundSwap;

/// <summary>
/// Ring buffer for audio arriving from the network.
/// - Fills up to the target level first (priming), then plays.
/// - On underrun it outputs silence and primes again.
/// - To compensate for clock drift between the two devices, if the lowest fill level in the last 0.5 s
///   is clearly above the target, the excess is dropped. This keeps latency from growing over time.
/// </summary>
sealed class JitterBuffer
{
    readonly object _lock = new();
    readonly float[] _ring;
    readonly int _channels;
    readonly int _windowFrames;
    readonly int _slackFrames;
    readonly int _sampleRate;
    int _readPos;
    int _count; // in samples
    bool _priming = true;
    int _targetFrames;
    int _windowMin = int.MaxValue;
    int _windowConsumed;

    public long Underruns;
    public long DroppedFrames;

    public JitterBuffer(int channels, int sampleRate, int capacityMs, int targetMs)
    {
        _channels = channels;
        _sampleRate = sampleRate;
        _ring = new float[sampleRate * capacityMs / 1000 * channels];
        _windowFrames = sampleRate / 2;
        _slackFrames = sampleRate * 8 / 1000;
        TargetMs = targetMs;
    }

    public int TargetMs
    {
        set { lock (_lock) _targetFrames = Math.Max(1, _sampleRate * value / 1000); }
    }

    public double BufferedMs
    {
        get { lock (_lock) return _count / _channels * 1000.0 / _sampleRate; }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            int len = _ring.Length;
            if (samples.Length > len) samples = samples[^len..];
            int n = samples.Length;
            int free = len - _count;
            if (n > free)
            {
                int drop = n - free;
                _readPos = (_readPos + drop) % len;
                _count -= drop;
                DroppedFrames += drop / _channels;
            }
            int writePos = (_readPos + _count) % len;
            int first = Math.Min(n, len - writePos);
            samples[..first].CopyTo(_ring.AsSpan(writePos));
            samples[first..].CopyTo(_ring.AsSpan(0));
            _count += n;
        }
    }

    /// <summary>Always fills the destination completely (any shortfall is silence).</summary>
    public void Read(Span<float> dest)
    {
        lock (_lock)
        {
            int frames = dest.Length / _channels;
            int level = _count / _channels;

            if (_priming)
            {
                if (level < _targetFrames)
                {
                    dest.Clear();
                    return;
                }
                _priming = false;
                _windowMin = int.MaxValue;
                _windowConsumed = 0;
            }

            if (level < frames)
            {
                Underruns++;
                _priming = true;
                int have = _count;
                CopyOut(dest[..have]);
                dest[have..].Clear();
                return;
            }

            CopyOut(dest);

            int after = _count / _channels;
            if (after < _windowMin) _windowMin = after;
            _windowConsumed += frames;
            if (_windowConsumed >= _windowFrames)
            {
                int excess = _windowMin - _targetFrames;
                if (excess > _slackFrames)
                {
                    int drop = excess - _slackFrames / 2;
                    Skip(drop * _channels);
                    DroppedFrames += drop;
                }
                _windowMin = int.MaxValue;
                _windowConsumed = 0;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _count = 0;
            _readPos = 0;
            _priming = true;
        }
    }

    void CopyOut(Span<float> dest)
    {
        int len = _ring.Length;
        int n = dest.Length;
        int first = Math.Min(n, len - _readPos);
        _ring.AsSpan(_readPos, first).CopyTo(dest);
        _ring.AsSpan(0, n - first).CopyTo(dest[first..]);
        _readPos = (_readPos + n) % len;
        _count -= n;
    }

    void Skip(int samples)
    {
        samples = Math.Min(samples, _count);
        _readPos = (_readPos + samples) % _ring.Length;
        _count -= samples;
    }
}
