package com.soundswap.app

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

class JitterBufferTest {
    private val rate = 48000
    private val framesPerMs = rate / 1000
    private val packetFrames = 240 // 5 ms, same as the network packets

    /** Samples 1, 2, 3, … so that order and loss can be checked after reading. */
    private fun ramp(count: Int, start: Int = 1) = ShortArray(count) { (start + it).toShort() }

    private fun JitterBuffer.write(src: ShortArray) = write(src, src.size)
    private fun JitterBuffer.read(dst: ShortArray) = read(dst, dst.size)

    private fun assertSilent(a: ShortArray) = a.forEach { assertEquals(0.toShort(), it) }

    @Test
    fun returnsSilenceUntilTargetLevelIsReached() {
        val jb = JitterBuffer(channels = 1, sampleRate = rate, capacityMs = 200, targetMs = 20)
        val dst = ShortArray(packetFrames) { -1 }

        jb.write(ramp(19 * framesPerMs))
        jb.read(dst)

        assertSilent(dst)
        assertEquals(19.0, jb.bufferedMs, 1e-9) // nothing consumed while priming
        assertEquals(0L, jb.underruns)
    }

    @Test
    fun startsPlaybackOnceTargetIsReachedInOrder() {
        val jb = JitterBuffer(1, rate, 200, 20)
        jb.write(ramp(20 * framesPerMs))

        val dst = ShortArray(packetFrames)
        jb.read(dst)
        assertArrayEquals(ramp(packetFrames), dst)
        jb.read(dst)
        assertArrayEquals(ramp(packetFrames, start = packetFrames + 1), dst)
        assertEquals(10.0, jb.bufferedMs, 1e-9)
    }

    @Test
    fun readFillsOnlyRequestedLength() {
        val jb = JitterBuffer(1, rate, 200, 5)
        jb.write(ramp(packetFrames))

        val dst = ShortArray(packetFrames + 10) { -1 }
        jb.read(dst, packetFrames)

        assertArrayEquals(ramp(packetFrames), dst.copyOfRange(0, packetFrames))
        dst.copyOfRange(packetFrames, dst.size).forEach { assertEquals((-1).toShort(), it) }
    }

    @Test
    fun underrunPlaysRemainderPadsSilenceAndReprimes() {
        val jb = JitterBuffer(1, rate, 200, 5)
        jb.write(ramp(packetFrames + 100))

        val dst = ShortArray(packetFrames)
        jb.read(dst) // primed, plays one full packet
        jb.read(dst) // only 100 frames left -> underrun

        assertEquals(1L, jb.underruns)
        assertArrayEquals(ramp(100, start = packetFrames + 1), dst.copyOfRange(0, 100))
        assertSilent(dst.copyOfRange(100, packetFrames))
        assertEquals(0.0, jb.bufferedMs, 0.0)

        // After an underrun the buffer waits for the target level again.
        jb.write(ramp(packetFrames - 1, start = 1000))
        dst.fill(-1)
        jb.read(dst)
        assertSilent(dst)

        jb.write(ramp(1, start = 5000))
        jb.read(dst)
        assertEquals(1000.toShort(), dst.first())
        assertEquals(5000.toShort(), dst.last())
        assertEquals(1L, jb.underruns)
    }

    @Test
    fun driftCompensationDropsExcessWhenLevelStaysAboveTarget() {
        // Sender runs "ahead": the buffer constantly holds 100 ms although the target is 20 ms.
        val targetMs = 20
        val jb = JitterBuffer(1, rate, 500, targetMs)
        jb.write(ramp(100 * framesPerMs))

        val dst = ShortArray(packetFrames)
        repeat(rate / 2 / packetFrames) { // the drift window is 0.5 s
            jb.read(dst)
            jb.write(ramp(packetFrames))
        }

        // Lowest level in the window was 95 ms; it is trimmed to target + slack/2 = 20 + 4 ms.
        val minLevel = 95 * framesPerMs
        val expectedAfter = (targetMs + 4) * framesPerMs
        assertEquals((minLevel - expectedAfter).toLong(), jb.droppedFrames)
        assertEquals((targetMs + 4 + 5).toDouble(), jb.bufferedMs, 1e-9)
        assertEquals(0L, jb.underruns)
    }

    @Test
    fun driftCompensationKeepsLevelWithinSlack() {
        // 8 ms of slack above the target is tolerated; 7 ms extra must not be dropped.
        val jb = JitterBuffer(1, rate, 500, 20)
        jb.write(ramp((20 + 7 + 5) * framesPerMs))

        val dst = ShortArray(packetFrames)
        repeat(2 * rate / packetFrames) { // 2 s, four windows
            jb.read(dst)
            jb.write(ramp(packetFrames))
        }

        assertEquals(0L, jb.droppedFrames)
        assertEquals(32.0, jb.bufferedMs, 1e-9)
    }

    @Test
    fun overflowDropsOldestSamplesAndKeepsNewest() {
        val jb = JitterBuffer(1, rate, capacityMs = 10, targetMs = 1) // 480-frame ring
        val capacity = 10 * framesPerMs

        jb.write(ramp(capacity - 40))
        jb.write(ramp(100, start = 10_000)) // 60 frames too many

        assertEquals(60L, jb.droppedFrames)
        assertEquals(10.0, jb.bufferedMs, 1e-9)

        val dst = ShortArray(capacity)
        jb.read(dst)
        assertEquals(61.toShort(), dst.first()) // samples 1..60 were dropped
        assertEquals(10_099.toShort(), dst.last())
    }

    @Test
    fun writeLargerThanCapacityKeepsTail() {
        val jb = JitterBuffer(1, rate, capacityMs = 10, targetMs = 1)
        val capacity = 10 * framesPerMs

        jb.write(ramp(capacity + 123))

        val dst = ShortArray(capacity)
        jb.read(dst)
        assertEquals(124.toShort(), dst.first())
        assertEquals((capacity + 123).toShort(), dst.last())
    }

    @Test
    fun stereoCountsFramesNotSamples() {
        val jb = JitterBuffer(channels = 2, sampleRate = rate, capacityMs = 200, targetMs = 10)
        jb.write(ramp(10 * framesPerMs * 2))
        assertEquals(10.0, jb.bufferedMs, 1e-9)

        val dst = ShortArray(packetFrames * 2)
        jb.read(dst)
        assertArrayEquals(ramp(packetFrames * 2), dst)
        assertEquals(5.0, jb.bufferedMs, 1e-9)
    }

    @Test
    fun ringWrapsAroundWithoutCorruptingOrder() {
        val jb = JitterBuffer(1, rate, capacityMs = 10, targetMs = 1) // 480-frame ring
        val dst = ShortArray(packetFrames)
        var next = 1
        repeat(20) { // 20 × 240 frames pass through a 480-frame ring
            jb.write(ramp(packetFrames, next))
            jb.read(dst)
            assertArrayEquals(ramp(packetFrames, next), dst)
            next += packetFrames
        }
        assertEquals(0L, jb.underruns)
        assertEquals(0L, jb.droppedFrames)
    }

    @Test
    fun clearEmptiesBufferAndReprimes() {
        val jb = JitterBuffer(1, rate, 200, 5)
        jb.write(ramp(packetFrames * 4))
        val dst = ShortArray(packetFrames)
        jb.read(dst)

        jb.clear()
        assertEquals(0.0, jb.bufferedMs, 0.0)

        jb.write(ramp(packetFrames - 1))
        dst.fill(-1)
        jb.read(dst)
        assertSilent(dst)
        assertEquals(0L, jb.underruns)
    }
}
