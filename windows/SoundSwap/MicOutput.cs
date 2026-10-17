using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundSwap;

/// <summary>
/// Plays the microphone audio from the phone into an output device. That device should be the
/// virtual cable's input (e.g. "CABLE Input"); Windows then shows "CABLE Output" as a microphone.
/// </summary>
sealed class MicOutput : IDisposable
{
    readonly JitterBuffer _jitter;
    readonly WasapiOut _out;
    float[] _floatBuf = new float[960];

    public volatile float Peak;

    public MicOutput(MMDevice device, int targetMs)
    {
        _jitter = new JitterBuffer(1, Protocol.SampleRate, 500, targetMs);
        var mix = device.AudioClient.MixFormat;

        ISampleProvider src = new JitterSampleProvider(_jitter);
        if (mix.Channels > 1) src = new MonoToMultiChannel(src, mix.Channels);
        if (mix.SampleRate != Protocol.SampleRate) src = new WdlResamplingSampleProvider(src, mix.SampleRate);

        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
        _out.Init(src);
        _out.Play();
    }

    public JitterBuffer Jitter => _jitter;

    public int TargetMs { set => _jitter.TargetMs = value; }

    /// <summary>s16le mono PCM.</summary>
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
        _jitter.Write(_floatBuf.AsSpan(0, n));
    }

    public void Dispose()
    {
        _out.Stop();
        _out.Dispose();
    }

    sealed class JitterSampleProvider(JitterBuffer jitter) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Protocol.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            jitter.Read(buffer.AsSpan(offset, count));
            return count;
        }
    }

    sealed class MonoToMultiChannel(ISampleProvider source, int channels) : ISampleProvider
    {
        float[] _mono = new float[1024];

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            int read = source.Read(_mono, 0, frames);
            for (int i = 0; i < read; i++)
                for (int c = 0; c < channels; c++)
                    buffer[offset + i * channels + c] = _mono[i];
            return read * channels;
        }
    }
}
