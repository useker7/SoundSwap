using System.Buffers.Binary;

namespace SoundSwap;

/// <summary>
/// SoundSwap UDP protokolü (bkz. README "Protokol").
/// Başlık 8 bayt: 'S' 'W' sürüm tip seq(uint32 LE). Ses: PCM s16le, 48 kHz.
/// </summary>
static class Protocol
{
    public const int Port = 50505;
    public const byte Version = 1;
    public const int HeaderSize = 8;
    public const int SampleRate = 48000;
    /// <summary>PC → telefon paket boyu: 5 ms.</summary>
    public const int FrameSamples = 240;

    public const byte FlagWantsPcAudio = 1;
    public const byte FlagSendsMic = 2;

    public enum PacketType : byte
    {
        Hello = 1,         // telefon → PC: [flags][cihaz adı utf8]
        HelloAck = 2,      // PC → telefon: [PC adı utf8]
        PcAudio = 3,       // PC → telefon: stereo s16le
        MicAudio = 4,      // telefon → PC: mono s16le
        Bye = 5,           // telefon → PC
        Discover = 6,      // telefon → yayın
        DiscoverReply = 7, // PC → telefon: [PC adı utf8]
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
