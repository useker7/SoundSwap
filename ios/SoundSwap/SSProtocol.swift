import Foundation

/// SoundSwap UDP protokolü — Windows ve Android tarafıyla aynı (bkz. README).
enum SSProtocol {
    static let port: UInt16 = 50505
    static let version: UInt8 = 1
    static let headerSize = 8
    static let sampleRate: Double = 48000
    /// Mikrofon paket boyu: 5 ms.
    static let micFrameSamples = 240

    static let flagWantsPcAudio: UInt8 = 1
    static let flagSendsMic: UInt8 = 2

    enum PacketType: UInt8 {
        case hello = 1, helloAck, pcAudio, micAudio, bye, discover, discoverReply
    }

    static func writeHeader(_ p: UnsafeMutablePointer<UInt8>, type: PacketType, seq: UInt32) {
        p[0] = 0x53 // 'S'
        p[1] = 0x57 // 'W'
        p[2] = version
        p[3] = type.rawValue
        p[4] = UInt8(truncatingIfNeeded: seq)
        p[5] = UInt8(truncatingIfNeeded: seq >> 8)
        p[6] = UInt8(truncatingIfNeeded: seq >> 16)
        p[7] = UInt8(truncatingIfNeeded: seq >> 24)
    }

    static func packet(_ type: PacketType, payload: [UInt8] = []) -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: headerSize)
        bytes.withUnsafeMutableBufferPointer { writeHeader($0.baseAddress!, type: type, seq: 0) }
        return bytes + payload
    }

    static func parse(_ p: UnsafePointer<UInt8>, length: Int) -> (type: PacketType, seq: UInt32)? {
        guard length >= headerSize, p[0] == 0x53, p[1] == 0x57, p[2] == version,
              let type = PacketType(rawValue: p[3]) else { return nil }
        let seq = UInt32(p[4]) | UInt32(p[5]) << 8 | UInt32(p[6]) << 16 | UInt32(p[7]) << 24
        return (type, seq)
    }
}
