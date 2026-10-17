package com.soundswap.app

/** SoundSwap UDP protokolü — Windows ve iOS tarafıyla aynı (bkz. README). */
object Protocol {
    const val PORT = 50505
    const val VERSION: Byte = 1
    const val HEADER_SIZE = 8
    const val SAMPLE_RATE = 48000
    /** Mikrofon paket boyu: 5 ms. */
    const val MIC_FRAME_SAMPLES = 240

    const val HELLO = 1
    const val HELLO_ACK = 2
    const val PC_AUDIO = 3
    const val MIC_AUDIO = 4
    const val BYE = 5
    const val DISCOVER = 6
    const val DISCOVER_REPLY = 7

    const val FLAG_WANTS_PC_AUDIO = 1
    const val FLAG_SENDS_MIC = 2

    fun writeHeader(buf: ByteArray, type: Int, seq: Int) {
        buf[0] = 'S'.code.toByte()
        buf[1] = 'W'.code.toByte()
        buf[2] = VERSION
        buf[3] = type.toByte()
        buf[4] = seq.toByte()
        buf[5] = (seq shr 8).toByte()
        buf[6] = (seq shr 16).toByte()
        buf[7] = (seq shr 24).toByte()
    }

    fun packet(type: Int, payload: ByteArray = ByteArray(0)): ByteArray {
        val buf = ByteArray(HEADER_SIZE + payload.size)
        writeHeader(buf, type, 0)
        payload.copyInto(buf, HEADER_SIZE)
        return buf
    }

    /** Geçerli bir SoundSwap paketiyse tipini, değilse -1 döndürür. */
    fun typeOf(buf: ByteArray, length: Int): Int {
        if (length < HEADER_SIZE || buf[0] != 'S'.code.toByte() || buf[1] != 'W'.code.toByte() || buf[2] != VERSION) return -1
        return buf[3].toInt() and 0xff
    }

    fun seqOf(buf: ByteArray): Int =
        (buf[4].toInt() and 0xff) or
            ((buf[5].toInt() and 0xff) shl 8) or
            ((buf[6].toInt() and 0xff) shl 16) or
            ((buf[7].toInt() and 0xff) shl 24)
}
