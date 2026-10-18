namespace SoundSwap.Tests;

public class MicMixerTests
{
    const int PacketFrames = 240; // 5 ms

    /// <summary>A network packet payload: s16le mono with every sample set to <paramref name="value"/>.</summary>
    static byte[] Pcm(short value, int samples = PacketFrames)
    {
        var b = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            b[i * 2] = (byte)value;
            b[i * 2 + 1] = (byte)(value >> 8);
        }
        return b;
    }

    static void Fill(MicMixer.Channel ch, short value, int packets)
    {
        for (int i = 0; i < packets; i++) ch.Push(Pcm(value));
    }

    [Fact]
    public void Mix_WithNoChannels_IsSilence()
    {
        var mixer = new MicMixer(targetMs: 10);
        var dest = new float[PacketFrames];
        Array.Fill(dest, 1f);

        mixer.Mix(dest);

        Assert.All(dest, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Mix_SumsAllChannels()
    {
        var mixer = new MicMixer(targetMs: 10);
        var a = mixer.Open();
        var b = mixer.Open();
        Fill(a, 8192, 4);  // 0.25
        Fill(b, 4096, 4);  // 0.125

        var dest = new float[PacketFrames];
        mixer.Mix(dest);

        Assert.Equal(2, mixer.ChannelCount);
        Assert.All(dest, s => Assert.Equal(0.375f, s, 4));
    }

    [Fact]
    public void Mix_ClipsToFullScale()
    {
        var mixer = new MicMixer(targetMs: 10);
        Fill(mixer.Open(), 24576, 4); // 0.75
        Fill(mixer.Open(), 24576, 4); // 0.75

        var dest = new float[PacketFrames];
        mixer.Mix(dest);

        Assert.All(dest, s => Assert.Equal(1f, s));
    }

    [Fact]
    public void Mix_ChannelStillPriming_DoesNotSilenceTheOthers()
    {
        var mixer = new MicMixer(targetMs: 10);
        var ready = mixer.Open();
        var late = mixer.Open();
        Fill(ready, 8192, 4); // above the 10 ms target
        Fill(late, 8192, 1);  // 5 ms: still priming, contributes silence

        var dest = new float[PacketFrames];
        mixer.Mix(dest);

        Assert.All(dest, s => Assert.Equal(0.25f, s, 4));
    }

    [Fact]
    public void Close_RemovesChannelFromMix()
    {
        var mixer = new MicMixer(targetMs: 10);
        var a = mixer.Open();
        var b = mixer.Open();
        Fill(a, 8192, 4);
        Fill(b, 8192, 4);

        mixer.Close(b);
        var dest = new float[PacketFrames];
        mixer.Mix(dest);

        Assert.Equal(1, mixer.ChannelCount);
        Assert.All(dest, s => Assert.Equal(0.25f, s, 4));
    }

    [Fact]
    public void Push_TracksPeak()
    {
        var mixer = new MicMixer(targetMs: 10);
        var ch = mixer.Open();

        ch.Push(Pcm(-16384));

        Assert.Equal(0.5f, ch.Peak, 4);
        Assert.Equal(0.5f, mixer.Peak, 4);
    }
}
