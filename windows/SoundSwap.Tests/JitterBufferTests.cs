namespace SoundSwap.Tests;

public class JitterBufferTests
{
    const int Rate = 48000;
    const int FramesPerMs = Rate / 1000;
    const int PacketFrames = 240; // 5 ms, same as the network packets

    /// <summary>Samples 1, 2, 3, … so that order and loss can be checked after reading.</summary>
    static float[] Ramp(int count, int start = 1)
    {
        var a = new float[count];
        for (int i = 0; i < count; i++) a[i] = start + i;
        return a;
    }

    [Fact]
    public void Read_ReturnsSilence_UntilTargetLevelIsReached()
    {
        var jb = new JitterBuffer(channels: 1, sampleRate: Rate, capacityMs: 200, targetMs: 20);
        var dest = new float[PacketFrames];

        jb.Write(Ramp(19 * FramesPerMs)); // 19 ms < 20 ms target
        Array.Fill(dest, -1f);
        jb.Read(dest);

        Assert.All(dest, s => Assert.Equal(0f, s));
        Assert.Equal(19.0, jb.BufferedMs, 3); // nothing consumed while priming
        Assert.Equal(0, jb.Underruns);
    }

    [Fact]
    public void Read_StartsPlayback_OnceTargetIsReached_InOrder()
    {
        var jb = new JitterBuffer(1, Rate, 200, 20);
        jb.Write(Ramp(20 * FramesPerMs));

        var dest = new float[PacketFrames];
        jb.Read(dest);
        Assert.Equal(Ramp(PacketFrames), dest);

        jb.Read(dest);
        Assert.Equal(Ramp(PacketFrames, start: PacketFrames + 1), dest);
        Assert.Equal(10.0, jb.BufferedMs, 3);
    }

    [Fact]
    public void Read_InterleavedStereo_CountsFramesNotSamples()
    {
        var jb = new JitterBuffer(channels: 2, sampleRate: Rate, capacityMs: 200, targetMs: 10);
        jb.Write(Ramp(10 * FramesPerMs * 2));
        Assert.Equal(10.0, jb.BufferedMs, 3);

        var dest = new float[PacketFrames * 2];
        jb.Read(dest);
        Assert.Equal(Ramp(PacketFrames * 2), dest);
        Assert.Equal(5.0, jb.BufferedMs, 3);
    }

    [Fact]
    public void Underrun_PlaysRemainder_PadsSilence_AndReprimes()
    {
        var jb = new JitterBuffer(1, Rate, 200, 5);
        jb.Write(Ramp(PacketFrames + 100));

        var dest = new float[PacketFrames];
        jb.Read(dest); // primed, plays one full packet
        jb.Read(dest); // only 100 frames left -> underrun

        Assert.Equal(1, jb.Underruns);
        Assert.Equal(Ramp(100, start: PacketFrames + 1), dest[..100]);
        Assert.All(dest[100..], s => Assert.Equal(0f, s));
        Assert.Equal(0.0, jb.BufferedMs);

        // After an underrun the buffer waits for the target level again.
        jb.Write(Ramp(PacketFrames - 1, start: 1000));
        Array.Fill(dest, -1f);
        jb.Read(dest);
        Assert.All(dest, s => Assert.Equal(0f, s));
        Assert.Equal(1, jb.Underruns);

        jb.Write(Ramp(1, start: 5000));
        jb.Read(dest);
        Assert.Equal(1000f, dest[0]);
        Assert.Equal(5000f, dest[^1]);
    }

    [Fact]
    public void DriftCompensation_DropsExcess_WhenLevelStaysAboveTarget()
    {
        // Sender runs "ahead": the buffer constantly holds 100 ms although the target is 20 ms.
        const int targetMs = 20;
        var jb = new JitterBuffer(1, Rate, 500, targetMs);
        jb.Write(Ramp(100 * FramesPerMs));

        var dest = new float[PacketFrames];
        int packetsPerWindow = Rate / 2 / PacketFrames; // the drift window is 0.5 s
        for (int i = 0; i < packetsPerWindow; i++)
        {
            jb.Read(dest);
            jb.Write(Ramp(PacketFrames));
        }

        // Lowest level in the window was 95 ms; it is trimmed to target + slack/2 = 20 + 4 ms.
        int minLevel = 95 * FramesPerMs;
        int expectedAfter = (targetMs + 4) * FramesPerMs;
        Assert.Equal(minLevel - expectedAfter, jb.DroppedFrames);
        Assert.Equal(targetMs + 4 + 5, jb.BufferedMs, 3); // + the packet written after the last read
        Assert.Equal(0, jb.Underruns);
    }

    [Fact]
    public void DriftCompensation_KeepsLevel_WithinSlack()
    {
        // 8 ms of slack above the target is tolerated; 7 ms extra must not be dropped.
        var jb = new JitterBuffer(1, Rate, 500, 20);
        jb.Write(Ramp((20 + 7 + 5) * FramesPerMs));

        var dest = new float[PacketFrames];
        for (int i = 0; i < 2 * Rate / PacketFrames; i++) // 2 s, four windows
        {
            jb.Read(dest);
            jb.Write(Ramp(PacketFrames));
        }

        Assert.Equal(0, jb.DroppedFrames);
        Assert.Equal(32.0, jb.BufferedMs, 3);
    }

    [Fact]
    public void Overflow_DropsOldestSamples_AndKeepsNewest()
    {
        var jb = new JitterBuffer(1, Rate, capacityMs: 10, targetMs: 1); // 480-frame ring
        int capacity = 10 * FramesPerMs;

        jb.Write(Ramp(capacity - 40));
        jb.Write(Ramp(100, start: 10_000)); // 60 frames too many

        Assert.Equal(60, jb.DroppedFrames);
        Assert.Equal(10.0, jb.BufferedMs, 3);

        var dest = new float[capacity];
        jb.Read(dest);
        Assert.Equal(61f, dest[0]); // samples 1..60 were dropped
        Assert.Equal(10_099f, dest[^1]);
    }

    [Fact]
    public void Overflow_WriteLargerThanCapacity_KeepsTail()
    {
        var jb = new JitterBuffer(1, Rate, capacityMs: 10, targetMs: 1);
        int capacity = 10 * FramesPerMs;

        jb.Write(Ramp(capacity + 123));

        var dest = new float[capacity];
        jb.Read(dest);
        Assert.Equal(124f, dest[0]);
        Assert.Equal(capacity + 123f, dest[^1]);
    }

    [Fact]
    public void Ring_WrapsAround_WithoutCorruptingOrder()
    {
        var jb = new JitterBuffer(1, Rate, capacityMs: 10, targetMs: 1); // 480-frame ring
        var dest = new float[PacketFrames];
        int next = 1, expected = 1;

        for (int i = 0; i < 20; i++) // 20 × 240 frames pass through a 480-frame ring
        {
            jb.Write(Ramp(PacketFrames, next));
            next += PacketFrames;
            jb.Read(dest);
            Assert.Equal(Ramp(PacketFrames, expected), dest);
            expected += PacketFrames;
        }
        Assert.Equal(0, jb.Underruns);
        Assert.Equal(0, jb.DroppedFrames);
    }

    [Fact]
    public void Clear_EmptiesBuffer_AndReprimes()
    {
        var jb = new JitterBuffer(1, Rate, 200, 5);
        jb.Write(Ramp(PacketFrames * 4));
        var dest = new float[PacketFrames];
        jb.Read(dest);

        jb.Clear();
        Assert.Equal(0.0, jb.BufferedMs);

        jb.Write(Ramp(PacketFrames - 1));
        Array.Fill(dest, -1f);
        jb.Read(dest);
        Assert.All(dest, s => Assert.Equal(0f, s));
        Assert.Equal(0, jb.Underruns);
    }
}
