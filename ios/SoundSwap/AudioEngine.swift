import AVFoundation
import UIKit

/// PC ile ses alışverişini yapan motor.
/// Çalma: AVAudioSourceNode jitter tamponundan çeker. Mikrofon: AVAudioSinkNode → 48 kHz → FIFO → UDP.
final class AudioEngine {
    struct Config {
        var host: String
        var playPcAudio: Bool
        var sendMic: Bool
        var jitterMs: Int
        var echoCancel: Bool
        /// Bluetooth kulaklığın mikrofonunu kullan (HFP; ses kalitesi düşer).
        var bluetoothMic: Bool
    }

    enum EngineError: LocalizedError {
        case badAddress
        var errorDescription: String? { "Invalid IP address (e.g. 192.168.1.20)" }
    }

    let config: Config

    private let engine = AVAudioEngine()
    private let jitter: JitterBuffer
    private let micFifo = SampleFIFO(capacity: 48_000, maxSamples: 48 * 60) // en fazla 60 ms birikir
    private let micSignal = DispatchSemaphore(value: 0)
    private let threads = DispatchGroup()
    private let deviceName: String
    private var socket: UDPSocket?
    private var sourceNode: AVAudioSourceNode?
    private var sinkNode: AVAudioSinkNode?
    private var observers: [NSObjectProtocol] = []
    private var running = false

    // Ses iş parçacığı için önceden ayrılmış tamponlar
    private static let maxFrames = 8192
    private let playScratch = UnsafeMutablePointer<Float>.allocate(capacity: maxFrames * 2)
    private let micIn = UnsafeMutablePointer<Float>.allocate(capacity: maxFrames)
    private let micOut = UnsafeMutablePointer<Float>.allocate(capacity: maxFrames * 4)
    private var micResampler: LinearResampler?
    private var micStride = 1

    // Arayüz için durum
    private(set) var pcName: String?
    private(set) var error: String?
    private(set) var playPeak: Float = 0
    private(set) var micPeak: Float = 0
    private(set) var packetsReceived = 0
    private(set) var packetsLost = 0
    private(set) var micPacketsSent = 0
    private var lastAck: TimeInterval = 0

    var connected: Bool { ProcessInfo.processInfo.systemUptime - lastAck < 3 }
    var bufferedMs: Double { jitter.bufferedMs }
    var underruns: Int { jitter.underruns }
    var droppedFrames: Int { jitter.droppedFrames }
    var outputLatencyMs: Double {
        let s = AVAudioSession.sharedInstance()
        return (s.outputLatency + s.ioBufferDuration) * 1000
    }

    init(config: Config) {
        self.config = config
        jitter = JitterBuffer(channels: 2, sampleRate: 48000, capacityMs: 500, targetMs: config.jitterMs)
        deviceName = UIDevice.current.name
    }

    deinit {
        playScratch.deallocate()
        micIn.deallocate()
        micOut.deallocate()
    }

    func setJitterMs(_ ms: Int) { jitter.setTargetMs(ms) }

    func start() throws {
        guard let sock = UDPSocket(host: config.host, port: SSProtocol.port) else { throw EngineError.badAddress }
        socket = sock
        try configureSession()
        try buildGraph()
        try engine.start()
        running = true

        spawn("ss-net") { [unowned self] in self.receiveLoop() }
        spawn("ss-hello") { [unowned self] in self.helloLoop() }
        if config.sendMic { spawn("ss-mic") { [unowned self] in self.micSendLoop() } }

        let nc = NotificationCenter.default
        observers.append(nc.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main) { [weak self] _ in
            self?.restartAudio()
        })
        observers.append(nc.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] note in
            guard let raw = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
                  AVAudioSession.InterruptionType(rawValue: raw) == .ended else { return }
            self?.restartAudio()
        })
    }

    func stop() {
        guard running else { return }
        running = false
        observers.forEach { NotificationCenter.default.removeObserver($0) }
        observers.removeAll()
        socket?.send(SSProtocol.packet(.bye))
        micSignal.signal()
        _ = threads.wait(timeout: .now() + 1)
        engine.stop()
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        socket = nil
    }

    private func spawn(_ name: String, _ body: @escaping () -> Void) {
        threads.enter()
        let t = Thread { [threads] in
            body()
            threads.leave()
        }
        t.name = name
        t.qualityOfService = .userInteractive
        t.start()
    }

    // MARK: - Ses oturumu ve grafiği

    /// Bluetooth kulaklık mikrofonu (HFP). Xcode 26 SDK'sında `.allowBluetooth` adı `.allowBluetoothHFP` oldu.
    private static var bluetoothHFP: AVAudioSession.CategoryOptions {
        #if compiler(>=6.2)
        return .allowBluetoothHFP
        #else
        return .allowBluetooth
        #endif
    }

    private func configureSession() throws {
        let s = AVAudioSession.sharedInstance()
        if config.sendMic {
            var options: AVAudioSession.CategoryOptions = [.defaultToSpeaker]
            options.insert(config.bluetoothMic ? Self.bluetoothHFP : .allowBluetoothA2DP)
            try s.setCategory(.playAndRecord, mode: config.echoCancel ? .voiceChat : .default, options: options)
        } else {
            try s.setCategory(.playback, mode: .default, options: [])
        }
        try? s.setPreferredSampleRate(48000)
        try? s.setPreferredIOBufferDuration(0.005)
        try s.setActive(true)
    }

    private func buildGraph() throws {
        if config.playPcAudio {
            let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48000, channels: 2, interleaved: false)!
            let node = AVAudioSourceNode(format: format) { [unowned self] _, _, frameCount, abl in
                self.render(frames: Int(frameCount), abl: abl)
                return noErr
            }
            engine.attach(node)
            engine.connect(node, to: engine.mainMixerNode, format: format)
            sourceNode = node
        }

        if config.sendMic {
            let input = engine.inputNode
            if config.echoCancel {
                try input.setVoiceProcessingEnabled(true)
            }
            let inFormat = input.outputFormat(forBus: 0)
            micStride = inFormat.isInterleaved ? Int(inFormat.channelCount) : 1
            micResampler = inFormat.sampleRate == 48000 ? nil : LinearResampler(inRate: inFormat.sampleRate, outRate: 48000)
            let node = AVAudioSinkNode { [unowned self] _, frameCount, abl in
                self.capture(frames: Int(frameCount), abl: abl)
                return noErr
            }
            engine.attach(node)
            engine.connect(input, to: node, format: inFormat)
            sinkNode = node
        }
        engine.prepare()
    }

    /// Kulaklık takılıp çıkınca, arama gelince vb. grafiği yeniden kurar.
    private func restartAudio() {
        guard running else { return }
        engine.stop()
        if let n = sourceNode { engine.detach(n) }
        if let n = sinkNode { engine.detach(n) }
        sourceNode = nil
        sinkNode = nil
        jitter.clear()
        do {
            try configureSession()
            try buildGraph()
            try engine.start()
            error = nil
        } catch {
            self.error = "Could not restart audio: \(error.localizedDescription)"
        }
    }

    // Gerçek zamanlı ses iş parçacığı: bellek ayırma yok.
    private func render(frames n: Int, abl: UnsafeMutablePointer<AudioBufferList>) {
        let list = UnsafeMutableAudioBufferListPointer(abl)
        guard list.count >= 2,
              let left = list[0].mData?.assumingMemoryBound(to: Float.self),
              let right = list[1].mData?.assumingMemoryBound(to: Float.self) else { return }
        let frames = min(n, Self.maxFrames)
        jitter.read(into: playScratch, count: frames * 2)
        var peak: Float = 0
        for i in 0..<frames {
            let l = playScratch[2 * i], r = playScratch[2 * i + 1]
            left[i] = l
            right[i] = r
            peak = max(peak, abs(l), abs(r))
        }
        if n > frames {
            (left + frames).update(repeating: 0, count: n - frames)
            (right + frames).update(repeating: 0, count: n - frames)
        }
        playPeak = max(peak, playPeak * 0.9)
    }

    // Gerçek zamanlı ses iş parçacığı: yalnızca FIFO'ya yazar, ağ işini mikrofon iş parçacığı yapar.
    private func capture(frames n: Int, abl: UnsafePointer<AudioBufferList>) {
        let list = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: abl))
        guard list.count >= 1, let p = list[0].mData?.assumingMemoryBound(to: Float.self) else { return }
        let frames = min(n, Self.maxFrames)
        for i in 0..<frames { micIn[i] = p[i * micStride] }
        if let rs = micResampler {
            let out = rs.process(micIn, count: frames, into: micOut)
            micFifo.write(micOut, count: out)
        } else {
            micFifo.write(micIn, count: frames)
        }
        micSignal.signal()
    }

    // MARK: - Ağ

    private func helloLoop() {
        var flags: UInt8 = 0
        if config.playPcAudio { flags |= SSProtocol.flagWantsPcAudio }
        if config.sendMic { flags |= SSProtocol.flagSendsMic }
        let packet = SSProtocol.packet(.hello, payload: [flags] + Array(deviceName.utf8))
        while running {
            socket?.send(packet)
            Thread.sleep(forTimeInterval: 0.5)
        }
    }

    private func receiveLoop() {
        let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: 4096)
        let samples = UnsafeMutablePointer<Float>.allocate(capacity: 2048)
        defer {
            buffer.deallocate()
            samples.deallocate()
        }
        var lastSeq: UInt32 = 0
        var hasSeq = false
        while running {
            guard let sock = socket else { break }
            let n = sock.receive(into: buffer, capacity: 4096)
            guard n > 0, let parsed = SSProtocol.parse(buffer, length: n) else { continue }
            let seq = parsed.seq
            switch parsed.type {
            case .helloAck:
                if !connected { jitter.clear() }
                lastAck = ProcessInfo.processInfo.systemUptime
                let nameBytes = UnsafeBufferPointer(start: buffer + SSProtocol.headerSize, count: n - SSProtocol.headerSize)
                pcName = String(decoding: nameBytes, as: UTF8.self)
            case .pcAudio:
                if hasSeq {
                    let diff = Int32(bitPattern: seq &- lastSeq)
                    if diff <= 0 && diff > -1000 { continue } // geç kalmış / yinelenen paket
                    if diff > 1 { packetsLost += Int(diff - 1) }
                }
                lastSeq = seq
                hasSeq = true
                let count = min((n - SSProtocol.headerSize) / 2, 2048) & ~1
                var o = SSProtocol.headerSize
                for i in 0..<count {
                    let v = Int16(bitPattern: UInt16(buffer[o]) | UInt16(buffer[o + 1]) << 8)
                    samples[i] = Float(v) / 32768
                    o += 2
                }
                jitter.write(samples, count: count)
                packetsReceived += 1
            default:
                break
            }
        }
    }

    private func micSendLoop() {
        let frame = SSProtocol.micFrameSamples
        let samples = UnsafeMutablePointer<Float>.allocate(capacity: frame)
        defer { samples.deallocate() }
        var packet = [UInt8](repeating: 0, count: SSProtocol.headerSize + frame * 2)
        var seq: UInt32 = 0
        while running {
            _ = micSignal.wait(timeout: .now() + .milliseconds(100))
            while running && micFifo.read(into: samples, count: frame) {
                var peak: Float = 0
                packet.withUnsafeMutableBufferPointer { buf in
                    let p = buf.baseAddress!
                    var o = SSProtocol.headerSize
                    for i in 0..<frame {
                        let f = max(-1, min(1, samples[i]))
                        peak = max(peak, abs(f))
                        let v = UInt16(bitPattern: Int16(f * 32767))
                        p[o] = UInt8(truncatingIfNeeded: v)
                        p[o + 1] = UInt8(truncatingIfNeeded: v >> 8)
                        o += 2
                    }
                    SSProtocol.writeHeader(p, type: .micAudio, seq: seq)
                }
                micPeak = max(peak, micPeak * 0.9)
                guard connected, let sock = socket else { continue }
                seq &+= 1
                sock.send(packet)
                micPacketsSent += 1
            }
        }
    }
}
