using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundSwap;

/// <summary>
/// Plays the (mixed) microphone audio from the phones into an output device. That device is the
/// virtual cable's input; Windows then shows the cable's other end as a microphone.
/// </summary>
sealed class MicOutput : IDisposable
{
    readonly WasapiOut _out;

    public MicOutput(MMDevice device, int targetMs)
    {
        Mixer = new MicMixer(targetMs);
        var mix = device.AudioClient.MixFormat;

        ISampleProvider src = new MixerSampleProvider(Mixer);
        if (mix.Channels > 1) src = new MonoToMultiChannel(src, mix.Channels);
        if (mix.SampleRate != Protocol.SampleRate) src = new WdlResamplingSampleProvider(src, mix.SampleRate);

        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
        _out.Init(src);
        _out.Play();
    }

    public MicMixer Mixer { get; }

    public void Dispose()
    {
        _out.Stop();
        _out.Dispose();
    }

    sealed class MixerSampleProvider(MicMixer mixer) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Protocol.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            mixer.Mix(buffer.AsSpan(offset, count));
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
