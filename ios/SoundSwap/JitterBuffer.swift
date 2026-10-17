import Foundation
import os

/// Ses iş parçacığında kullanılabilecek kadar hafif kilit.
final class UnfairLock {
    private let p: UnsafeMutablePointer<os_unfair_lock>

    init() {
        p = .allocate(capacity: 1)
        p.initialize(to: os_unfair_lock())
    }

    deinit {
        p.deinitialize(count: 1)
        p.deallocate()
    }

    @inline(__always) func lock() { os_unfair_lock_lock(p) }
    @inline(__always) func unlock() { os_unfair_lock_unlock(p) }
}

/// Ağdan gelen sesi tamponlayan halka tampon (Windows/Android'deki JitterBuffer ile aynı mantık).
/// - Başta hedef seviyeye kadar dolar, sonra çalar; boşalırsa sessizlik verip yeniden dolar.
/// - Saat kaymasına karşı: son 0,5 sn'deki en düşük doluluk hedefi belirgin aşarsa fazlalık atılır.
final class JitterBuffer {
    private let lock = UnfairLock()
    private let ring: UnsafeMutablePointer<Float>
    private let capacity: Int
    private let channels: Int
    private let sampleRate: Int
    private let windowFrames: Int
    private let slackFrames: Int
    private var readPos = 0
    private var count = 0 // örnek cinsinden
    private var priming = true
    private var targetFrames = 1
    private var windowMin = Int.max
    private var windowConsumed = 0
    private(set) var underruns = 0
    private(set) var droppedFrames = 0

    init(channels: Int, sampleRate: Int, capacityMs: Int, targetMs: Int) {
        self.channels = channels
        self.sampleRate = sampleRate
        capacity = sampleRate * capacityMs / 1000 * channels
        ring = .allocate(capacity: capacity)
        ring.initialize(repeating: 0, count: capacity)
        windowFrames = sampleRate / 2
        slackFrames = sampleRate * 8 / 1000
        setTargetMs(targetMs)
    }

    deinit {
        ring.deallocate()
    }

    func setTargetMs(_ ms: Int) {
        lock.lock()
        targetFrames = max(1, sampleRate * ms / 1000)
        lock.unlock()
    }

    var bufferedMs: Double {
        lock.lock()
        defer { lock.unlock() }
        return Double(count / channels) * 1000 / Double(sampleRate)
    }

    func write(_ src: UnsafePointer<Float>, count length: Int) {
        lock.lock()
        defer { lock.unlock() }
        var src = src
        var n = length
        if n > capacity {
            src += n - capacity
            n = capacity
        }
        let free = capacity - count
        if n > free {
            let drop = n - free
            readPos = (readPos + drop) % capacity
            count -= drop
            droppedFrames += drop / channels
        }
        let writePos = (readPos + count) % capacity
        let first = min(n, capacity - writePos)
        (ring + writePos).update(from: src, count: first)
        ring.update(from: src + first, count: n - first)
        count += n
    }

    /// dst'nin ilk `length` örneğini her zaman doldurur (eksik kısım sessizlik).
    func read(into dst: UnsafeMutablePointer<Float>, count length: Int) {
        lock.lock()
        defer { lock.unlock() }
        let frames = length / channels
        let level = count / channels

        if priming {
            if level < targetFrames {
                dst.update(repeating: 0, count: length)
                return
            }
            priming = false
            windowMin = Int.max
            windowConsumed = 0
        }

        if level < frames {
            underruns += 1
            priming = true
            let have = count
            copyOut(dst, have)
            (dst + have).update(repeating: 0, count: length - have)
            return
        }

        copyOut(dst, length)

        let after = count / channels
        if after < windowMin { windowMin = after }
        windowConsumed += frames
        if windowConsumed >= windowFrames {
            let excess = windowMin - targetFrames
            if excess > slackFrames {
                let drop = excess - slackFrames / 2
                skip(drop * channels)
                droppedFrames += drop
            }
            windowMin = Int.max
            windowConsumed = 0
        }
    }

    func clear() {
        lock.lock()
        count = 0
        readPos = 0
        priming = true
        lock.unlock()
    }

    private func copyOut(_ dst: UnsafeMutablePointer<Float>, _ n: Int) {
        let first = min(n, capacity - readPos)
        dst.update(from: ring + readPos, count: first)
        (dst + first).update(from: ring, count: n - first)
        readPos = (readPos + n) % capacity
        count -= n
    }

    private func skip(_ samples: Int) {
        let s = min(samples, count)
        readPos = (readPos + s) % capacity
        count -= s
    }
}

/// Mikrofon için basit FIFO: ses iş parçacığı yazar, ağ iş parçacığı 5 ms'lik bloklar okur.
/// Gecikme birikmesin diye `maxSamples` üstü en eski veriler atılır.
final class SampleFIFO {
    private let lock = UnfairLock()
    private let ring: UnsafeMutablePointer<Float>
    private let capacity: Int
    private let maxSamples: Int
    private var readPos = 0
    private var count = 0

    init(capacity: Int, maxSamples: Int) {
        self.capacity = capacity
        self.maxSamples = min(maxSamples, capacity)
        ring = .allocate(capacity: capacity)
        ring.initialize(repeating: 0, count: capacity)
    }

    deinit {
        ring.deallocate()
    }

    func write(_ src: UnsafePointer<Float>, count n: Int) {
        lock.lock()
        defer { lock.unlock() }
        for i in 0..<n {
            ring[(readPos + count) % capacity] = src[i]
            if count < capacity { count += 1 } else { readPos = (readPos + 1) % capacity }
        }
        if count > maxSamples {
            let drop = count - maxSamples
            readPos = (readPos + drop) % capacity
            count -= drop
        }
    }

    /// Yeterli veri varsa `n` örnek okur ve true döner.
    func read(into dst: UnsafeMutablePointer<Float>, count n: Int) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard count >= n else { return false }
        for i in 0..<n {
            dst[i] = ring[(readPos + i) % capacity]
        }
        readPos = (readPos + n) % capacity
        count -= n
        return true
    }
}

/// Akış halinde doğrusal örnekleme hızı dönüştürücü (mono).
final class LinearResampler {
    private let step: Double
    private var prev: Float = 0
    private var pos: Double = 0

    init(inRate: Double, outRate: Double) {
        step = inRate / outRate
    }

    func maxOutput(_ inFrames: Int) -> Int { Int((Double(inFrames) / step).rounded(.up)) + 2 }

    /// Girdi kavramsal olarak [önceki örnek, x0, x1, ...]; `pos` bu dizideki konumdur.
    func process(_ input: UnsafePointer<Float>, count n: Int, into output: UnsafeMutablePointer<Float>) -> Int {
        guard n > 0 else { return 0 }
        var o = 0
        while pos < Double(n) {
            let i = Int(pos)
            let f = Float(pos - Double(i))
            let a = i == 0 ? prev : input[i - 1]
            let b = input[i]
            output[o] = a + (b - a) * f
            o += 1
            pos += step
        }
        pos -= Double(n)
        prev = input[n - 1]
        return o
    }
}
