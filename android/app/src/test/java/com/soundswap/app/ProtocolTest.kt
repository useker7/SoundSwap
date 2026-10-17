package com.soundswap.app

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

class ProtocolTest {
    @Test
    fun writeHeaderProducesDocumentedLayout() {
        val buf = ByteArray(Protocol.HEADER_SIZE)
        Protocol.writeHeader(buf, Protocol.PC_AUDIO, 0x12345678)

        val expected = byteArrayOf('S'.code.toByte(), 'W'.code.toByte(), 1, 3, 0x78, 0x56, 0x34, 0x12)
        assertArrayEquals(expected, buf)
    }

    @Test
    fun sequenceRoundTripsIncludingHighBit() {
        val buf = ByteArray(Protocol.HEADER_SIZE)
        for (seq in intArrayOf(0, 1, 0x0201, Int.MAX_VALUE, Int.MIN_VALUE, -1)) {
            Protocol.writeHeader(buf, Protocol.MIC_AUDIO, seq)
            assertEquals(Protocol.MIC_AUDIO, Protocol.typeOf(buf, buf.size))
            assertEquals(seq, Protocol.seqOf(buf))
        }
    }

    @Test
    fun packetPrependsHeaderToPayload() {
        val payload = "Pixel".toByteArray()
        val p = Protocol.packet(Protocol.DISCOVER_REPLY, payload)

        assertEquals(Protocol.HEADER_SIZE + payload.size, p.size)
        assertEquals(Protocol.DISCOVER_REPLY, Protocol.typeOf(p, p.size))
        assertEquals("Pixel", String(p, Protocol.HEADER_SIZE, payload.size))
    }

    @Test
    fun typeOfRejectsInvalidPackets() {
        val valid = Protocol.packet(Protocol.HELLO_ACK)
        assertEquals(Protocol.HELLO_ACK, Protocol.typeOf(valid, valid.size))

        assertEquals(-1, Protocol.typeOf(valid, Protocol.HEADER_SIZE - 1)) // too short
        assertEquals(-1, Protocol.typeOf(valid.copyOf().also { it[0] = 'X'.code.toByte() }, valid.size))
        assertEquals(-1, Protocol.typeOf(valid.copyOf().also { it[1] = 'X'.code.toByte() }, valid.size))
        assertEquals(-1, Protocol.typeOf(valid.copyOf().also { it[2] = 2 }, valid.size)) // version
    }
}
