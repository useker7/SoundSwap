namespace SoundSwap.Tests;

public class ProtocolTests
{
    [Fact]
    public void WriteHeader_ProducesDocumentedLayout()
    {
        var buf = new byte[Protocol.HeaderSize];
        Protocol.WriteHeader(buf, Protocol.PacketType.PcAudio, 0x12345678);

        Assert.Equal(new byte[] { (byte)'S', (byte)'W', 1, 3, 0x78, 0x56, 0x34, 0x12 }, buf);
    }

    [Theory]
    [InlineData((byte)Protocol.PacketType.Hello, 0u)]
    [InlineData((byte)Protocol.PacketType.MicAudio, 1u)]
    [InlineData((byte)Protocol.PacketType.DiscoverReply, 0xFFFF_FFFFu)]
    [InlineData((byte)Protocol.PacketType.Bye, 0x8000_0000u)]
    public void TryParse_RoundTripsTypeAndSequence(byte rawType, uint seq)
    {
        // The enum is internal, so the test data passes its underlying byte value.
        var type = (Protocol.PacketType)rawType;
        var buf = new byte[Protocol.HeaderSize + 960];
        Protocol.WriteHeader(buf, type, seq);

        Assert.True(Protocol.TryParse(buf, out var parsedType, out var parsedSeq));
        Assert.Equal(type, parsedType);
        Assert.Equal(seq, parsedSeq);
    }

    [Fact]
    public void TryParse_ReadsSequenceAsLittleEndian()
    {
        byte[] buf = { (byte)'S', (byte)'W', 1, 4, 0x01, 0x02, 0x00, 0x00 };

        Assert.True(Protocol.TryParse(buf, out var type, out var seq));
        Assert.Equal(Protocol.PacketType.MicAudio, type);
        Assert.Equal(0x0201u, seq);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { (byte)'S', (byte)'W', 1, 3, 0, 0, 0 })]       // shorter than the header
    [InlineData(new byte[] { (byte)'X', (byte)'W', 1, 3, 0, 0, 0, 0 })]    // bad magic
    [InlineData(new byte[] { (byte)'S', (byte)'X', 1, 3, 0, 0, 0, 0 })]    // bad magic
    [InlineData(new byte[] { (byte)'S', (byte)'W', 2, 3, 0, 0, 0, 0 })]    // unknown version
    public void TryParse_RejectsInvalidPackets(byte[] buf)
    {
        Assert.False(Protocol.TryParse(buf, out _, out _));
    }

    [Fact]
    public void PcAudioPacket_Is5msOfStereoPcm()
    {
        // 240 frames × 2 channels × 2 bytes (s16le) = 960 bytes of payload at 48 kHz.
        Assert.Equal(5.0, Protocol.FrameSamples * 1000.0 / Protocol.SampleRate);
        Assert.Equal(960, Protocol.FrameSamples * 2 * sizeof(short));
    }
}
