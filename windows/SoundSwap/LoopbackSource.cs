using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundSwap;

/// <summary>
/// Bir çıkış cihazında çalan her şeyi WASAPI loopback ile yakalar, 48 kHz stereo float'a çevirir
/// ve 5 ms'lik (240 kare) bloklar halinde teslim eder.
/// NAudio'nun hazır WasapiLoopbackCapture'ı 100 ms tampon / 50 ms uyku kullandığı için
/// burada 1 ms aralıkla yoklayan kendi döngümüz var.
/// </summary>
sealed class LoopbackSource : IDisposable
{
    public delegate void FrameHandler(ReadOnlySpan<float> stereo48k);

    static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    readonly string _deviceId;
    readonly FrameHandler _onFrame;
    Thread? _thread;
    volatile bool _running;
    WasapiOut? _silence;

    readonly float[] _frame = new float[Protocol.FrameSamples * 2];
    int _frameFill; // örnek cinsinden

    public volatile float Peak;
    public volatile string? Error;

    public LoopbackSource(string deviceId, FrameHandler onFrame)
    {
        _deviceId = deviceId;
        _onFrame = onFrame;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "loopback" };
        _thread.Start();
    }

    void Run()
    {
        timeBeginPeriod(1);
        AudioClient? client = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_deviceId);

            // Hiçbir şey çalmazken loopback veri üretmez; cihazı sessizlikle sürekli açık tutarak
            // akışın kesintisiz ve cihaz saatiyle düzenli gelmesini sağlıyoruz.
            var mix = device.AudioClient.MixFormat;
            _silence = new WasapiOut(device, AudioClientShareMode.Shared, false, 50);
            _silence.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels)));
            _silence.Play();

            client = device.AudioClient;
            var fmt = client.MixFormat;
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback,
                20 * 10_000, 0, fmt, Guid.Empty);
            var capture = client.AudioCaptureClient;

            var kind = GetSampleKind(fmt);
            var resampler = fmt.SampleRate != Protocol.SampleRate
                ? new LinearResampler(2, fmt.SampleRate, Protocol.SampleRate)
                : null;
            var stereo = new float[4096 * 2];
            var resampled = new float[4096 * 2 * 4];

            client.Start();
            while (_running)
            {
                Thread.Sleep(1);
                while (_running && capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out var flags);
                    if (stereo.Length < frames * 2) stereo = new float[frames * 2];
                    var span = stereo.AsSpan(0, frames * 2);
                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                        span.Clear();
                    else
                        ToStereo(data, frames, fmt.Channels, kind, span);
                    capture.ReleaseBuffer(frames);

                    if (resampler != null)
                    {
                        int needed = resampler.MaxOutput(frames) * 2;
                        if (resampled.Length < needed) resampled = new float[needed];
                        int outFrames = resampler.Process(span, resampled);
                        Emit(resampled.AsSpan(0, outFrames * 2));
                    }
                    else
                    {
                        Emit(span);
                    }
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            client?.Dispose();
            _silence?.Stop();
            _silence?.Dispose();
            timeEndPeriod(1);
        }
    }

    void Emit(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        while (samples.Length > 0)
        {
            int take = Math.Min(samples.Length, _frame.Length - _frameFill);
            samples[..take].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            samples = samples[take..];
            if (_frameFill == _frame.Length)
            {
                foreach (var s in _frame) peak = Math.Max(peak, Math.Abs(s));
                _onFrame(_frame);
                _frameFill = 0;
            }
        }
        Peak = Math.Max(peak, Peak * 0.9f);
    }

    enum SampleKind { Float32, Int16, Int24, Int32 }

    static SampleKind GetSampleKind(WaveFormat fmt)
    {
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
            || (fmt is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
        if (isFloat && fmt.BitsPerSample == 32) return SampleKind.Float32;
        return fmt.BitsPerSample switch
        {
            16 => SampleKind.Int16,
            24 => SampleKind.Int24,
            32 => SampleKind.Int32,
            _ => throw new NotSupportedException($"Unsupported audio format: {fmt}"),
        };
    }

    /// <summary>Herhangi bir kanal düzenini stereo float'a indirger (5.1/7.1 için basit downmix).</summary>
    static unsafe void ToStereo(IntPtr data, int frames, int channels, SampleKind kind, Span<float> dest)
    {
        const float k = 0.7071f;
        byte* p = (byte*)data;
        int bytesPerSample = kind switch { SampleKind.Int16 => 2, SampleKind.Int24 => 3, _ => 4 };
        int stride = bytesPerSample * channels;
        for (int i = 0; i < frames; i++)
        {
            byte* f = p + i * stride;
            float l = Sample(f, 0), r = channels > 1 ? Sample(f, 1) : l;
            if (channels > 2)
            {
                float c = Sample(f, 2) * k;
                l += c;
                r += c;
            }
            if (channels > 5) { l += Sample(f, 4) * k; r += Sample(f, 5) * k; }
            if (channels > 7) { l += Sample(f, 6) * k; r += Sample(f, 7) * k; }
            dest[i * 2] = Math.Clamp(l, -1f, 1f);
            dest[i * 2 + 1] = Math.Clamp(r, -1f, 1f);
        }

        float Sample(byte* frame, int ch)
        {
            byte* s = frame + ch * bytesPerSample;
            return kind switch
            {
                SampleKind.Float32 => *(float*)s,
                SampleKind.Int16 => *(short*)s / 32768f,
                SampleKind.Int24 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
                _ => *(int*)s / 2147483648f,
            };
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
}

/// <summary>Akış halinde doğrusal örnekleme hızı dönüştürücü (interleaved).</summary>
sealed class LinearResampler
{
    readonly int _channels;
    readonly double _step;
    readonly float[] _prev;
    double _pos;

    public LinearResampler(int channels, int inRate, int outRate)
    {
        _channels = channels;
        _step = (double)inRate / outRate;
        _prev = new float[channels];
    }

    public int MaxOutput(int inFrames) => (int)Math.Ceiling(inFrames / _step) + 2;

    /// <summary>Girdi dizisi kavramsal olarak [önceki kare, x0, x1, ...]; _pos bu dizide konumdur.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int ch = _channels;
        int n = input.Length / ch;
        if (n == 0) return 0;
        int o = 0;
        while (_pos < n)
        {
            int i = (int)_pos;
            float f = (float)(_pos - i);
            for (int c = 0; c < ch; c++)
            {
                float a = i == 0 ? _prev[c] : input[(i - 1) * ch + c];
                float b = input[i * ch + c];
                output[o * ch + c] = a + (b - a) * f;
            }
            o++;
            _pos += _step;
        }
        _pos -= n;
        input.Slice((n - 1) * ch, ch).CopyTo(_prev);
        return o;
    }
}
