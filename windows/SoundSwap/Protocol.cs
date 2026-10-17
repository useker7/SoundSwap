using System.Buffers.Binary;

namespace SoundSwap;

/// <summary>
/// SoundSwap UDP protocol (see README, "Protocol").
/// 8-byte header: 'S' 'W' version type seq(uint32 LE). Audio: PCM s16le, 48 kHz.
/// </summary>
static class Protocol
{
    public const int Port = 50505;
    public const byte Version = 1;
    public const int HeaderSize = 8;
    public const int SampleRate = 48000;
    /// <summary>PC → phone packet length: 5 ms.</summary>
    public const int FrameSamples = 240;

    public const byte FlagWantsPcAudio = 1;
    public const byte FlagSendsMic = 2;

    public enum PacketType : byte
    {
        Hello = 1,         // phone → PC: [flags][device name utf8]
        HelloAck = 2,      // PC → phone: [PC name utf8]
        PcAudio = 3,       // PC → phone: stereo s16le
        MicAudio = 4,      // phone → PC: mono s16le
        Bye = 5,           // phone → PC
        Discover = 6,      // phone → broadcast
        DiscoverReply = 7, // PC → phone: [PC name utf8]
    }

    public static void WriteHeader(Span<byte> buf, PacketType type, uint seq)
    {
        buf[0] = (byte)'S';
        buf[1] = (byte)'W';
        buf[2] = Version;
        buf[3] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], seq);
    }

    public static bool TryParse(ReadOnlySpan<byte> buf, out PacketType type, out uint seq)
    {
        type = 0;
        seq = 0;
        if (buf.Length < HeaderSize || buf[0] != 'S' || buf[1] != 'W' || buf[2] != Version)
            return false;
        type = (PacketType)buf[3];
        seq = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]);
        return true;
    }
}
