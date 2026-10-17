package com.soundswap.app

/**
 * Ağdan gelen sesi tamponlayan halka tampon (Windows tarafındaki JitterBuffer.cs ile aynı mantık).
 * - Başta hedef seviyeye kadar dolar, sonra çalar; boşalırsa sessizlik verip yeniden dolar.
 * - Saat kaymasına karşı: son 0,5 sn'deki en düşük doluluk hedefi belirgin aşarsa fazlalık atılır.
 */
class JitterBuffer(private val channels: Int, private val sampleRate: Int, capacityMs: Int, targetMs: Int) {
    private val ring = ShortArray(sampleRate * capacityMs / 1000 * channels)
    private val windowFrames = sampleRate / 2
    private val slackFrames = sampleRate * 8 / 1000
    private var readPos = 0
    private var count = 0 // örnek cinsinden
    private var priming = true
    private var targetFrames = 0
    private var windowMin = Int.MAX_VALUE
    private var windowConsumed = 0

    @Volatile var underruns = 0L
        private set
    @Volatile var droppedFrames = 0L
        private set

    init {
        setTargetMs(targetMs)
    }

    @Synchronized
    fun setTargetMs(ms: Int) {
        targetFrames = maxOf(1, sampleRate * ms / 1000)
    }

    val bufferedMs: Double
        @Synchronized get() = count / channels * 1000.0 / sampleRate

    @Synchronized
    fun write(src: ShortArray, length: Int) {
        val len = ring.size
        var off = 0
        var n = length
        if (n > len) {
            off = n - len
            n = len
        }
        val free = len - count
        if (n > free) {
            val drop = n - free
            readPos = (readPos + drop) % len
            count -= drop
            droppedFrames += (drop / channels).toLong()
        }
        val writePos = (readPos + count) % len
        val first = minOf(n, len - writePos)
        System.arraycopy(src, off, ring, writePos, first)
        System.arraycopy(src, off + first, ring, 0, n - first)
        count += n
    }

    /** dst'nin ilk [length] örneğini her zaman doldurur (eksik kısım sessizlik). */
    @Synchronized
    fun read(dst: ShortArray, length: Int) {
        val frames = length / channels
        val level = count / channels

        if (priming) {
            if (level < targetFrames) {
                dst.fill(0, 0, length)
                return
            }
            priming = false
            windowMin = Int.MAX_VALUE
            windowConsumed = 0
        }

        if (level < frames) {
            underruns++
            priming = true
            val have = count
            copyOut(dst, have)
            dst.fill(0, have, length)
            return
        }

        copyOut(dst, length)

        val after = count / channels
        if (after < windowMin) windowMin = after
        windowConsumed += frames
        if (windowConsumed >= windowFrames) {
            val excess = windowMin - targetFrames
            if (excess > slackFrames) {
                val drop = excess - slackFrames / 2
                skip(drop * channels)
                droppedFrames += drop.toLong()
            }
            windowMin = Int.MAX_VALUE
            windowConsumed = 0
        }
    }

    @Synchronized
    fun clear() {
        count = 0
        readPos = 0
        priming = true
    }

    private fun copyOut(dst: ShortArray, n: Int) {
        val len = ring.size
        val first = minOf(n, len - readPos)
        System.arraycopy(ring, readPos, dst, 0, first)
        System.arraycopy(ring, 0, dst, first, n - first)
        readPos = (readPos + n) % len
        count -= n
    }

    private fun skip(samples: Int) {
        val s = minOf(samples, count)
        readPos = (readPos + s) % ring.size
        count -= s
    }
}
