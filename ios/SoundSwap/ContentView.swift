import AVFoundation
import SwiftUI

extension Color {
    /// Same brand color as the Windows and Android apps (#2563EB).
    static let brand = Color(red: 0x25 / 255, green: 0x63 / 255, blue: 0xEB / 255)
}

/// Manages the engine and refreshes its state for the UI 10 times per second.
final class EngineModel: ObservableObject {
    @Published private(set) var running = false
    @Published private(set) var connected = false
    @Published private(set) var title = ""
    @Published private(set) var detail = ""
    @Published private(set) var hasError = false
    @Published private(set) var stats = ""
    @Published private(set) var playLevel = 0.0
    @Published private(set) var micLevel = 0.0
    @Published var alert: String?

    private(set) var engine: AudioEngine?
    private var timer: Timer?

    func connect(_ config: AudioEngine.Config) {
        if config.sendMic {
            AVAudioSession.sharedInstance().requestRecordPermission { granted in
                DispatchQueue.main.async {
                    var c = config
                    if !granted {
                        self.alert = "Microphone permission denied; only PC audio will play. You can allow it in Settings > SoundSwap."
                        c.sendMic = false
                    }
                    self.launch(c)
                }
            }
        } else {
            launch(config)
        }
    }

    /// Applies settings changed while streaming (the engine restarts with the new settings).
    func restartIfRunning(_ config: AudioEngine.Config) {
        guard let e = engine else { return }
        var c = config
        c.host = e.config.host
        stop()
        connect(c)
    }

    private func launch(_ config: AudioEngine.Config) {
        engine?.stop()
        let e = AudioEngine(config: config)
        do {
            try e.start()
        } catch {
            e.stop()
            alert = "Could not start: \(error.localizedDescription)"
            return
        }
        engine = e
        running = true
        UIApplication.shared.isIdleTimerDisabled = true
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in self?.refresh() }
        refresh()
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        engine?.stop()
        engine = nil
        running = false
        UIApplication.shared.isIdleTimerDisabled = false
        refresh()
    }

    private func refresh() {
        guard let e = engine else {
            connected = false
            stats = ""
            playLevel = 0
            micLevel = 0
            return
        }
        connected = e.connected
        if let err = e.error {
            hasError = true
            title = "Connection problem"
            detail = err
        } else if e.connected {
            hasError = false
            title = e.pcName ?? e.config.host
            detail = "Connected · \(e.config.host)"
        } else {
            hasError = false
            title = "Connecting…"
            detail = "Trying to reach \(e.config.host)"
        }
        let latency = e.config.playPcAudio ? e.bufferedMs + e.outputLatencyMs : 0
        stats = String(format: "Latency ≈ %.0f ms · Lost %d · Underruns %d", latency, e.packetsLost, e.underruns)
        playLevel = Self.meter(e.playPeak)
        micLevel = Self.meter(e.micPeak)
    }

    private static func meter(_ peak: Float) -> Double {
        guard peak > 0.0001 else { return 0 }
        return min(1, max(0, (20 * log10(Double(peak)) + 60) / 60))
    }
}

struct ContentView: View {
    @AppStorage("host") private var lastHost = ""
    @AppStorage("play") private var play = true
    @AppStorage("mic") private var mic = true
    @AppStorage("aec") private var echoCancel = false
    @AppStorage("btMic") private var bluetoothMic = false
    @AppStorage("jitter") private var jitter = 30.0
    @StateObject private var model = EngineModel()
    @StateObject private var browser = PcBrowser()
    @State private var showManual = false
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        NavigationView {
            List {
                Section {
                    EmptyView()
                } header: {
                    HeaderView()
                        .textCase(nil)
                        .padding(.top, 8)
                        .padding(.bottom, 4)
                }

                if model.running {
                    connectionSection
                } else {
                    computersSection
                }

                Section {
                    toggleRow(icon: "speaker.wave.2.fill", title: "Play PC audio",
                              subtitle: "Through the phone speaker or connected headphones", isOn: $play)
                    if model.running && play { LevelBar(level: model.playLevel) }
                    toggleRow(icon: "mic.fill", title: "Send microphone",
                              subtitle: "Shows up on the PC as “SoundSwap Microphone”", isOn: $mic)
                    if model.running && mic { LevelBar(level: model.micLevel) }
                } header: {
                    Text("Audio")
                }

                Section {
                    Toggle(isOn: $echoCancel) {
                        SettingText(title: "Echo cancellation", subtitle: "Turn on when listening on the speaker and talking into the phone")
                    }
                    .disabled(!mic)
                    Toggle(isOn: $bluetoothMic) {
                        SettingText(title: "Bluetooth headset microphone", subtitle: "Audio quality drops to phone-call level")
                    }
                    .disabled(!mic)
                    VStack(alignment: .leading, spacing: 8) {
                        HStack {
                            Text("Audio buffer")
                            Spacer()
                            Text("\(Int(jitter)) ms").foregroundColor(.secondary)
                        }
                        Slider(value: $jitter, in: 10...150, step: 5)
                    }
                    .padding(.vertical, 4)
                } header: {
                    Text("Advanced")
                } footer: {
                    Text("A smaller buffer means less delay; increase it if the audio stutters. For the lowest latency use 5 GHz Wi-Fi and wired headphones.")
                }
            }
            .listStyle(.insetGrouped)
            .navigationBarHidden(true)
        }
        .navigationViewStyle(.stack)
        .tint(.brand)
        .onAppear { browser.start() }
        .onChange(of: scenePhase) { phase in
            if phase == .active { browser.start() } else if phase == .background { browser.stop() }
        }
        .onChange(of: play) { _ in model.restartIfRunning(config(host: lastHost)) }
        .onChange(of: mic) { _ in model.restartIfRunning(config(host: lastHost)) }
        .onChange(of: echoCancel) { _ in model.restartIfRunning(config(host: lastHost)) }
        .onChange(of: bluetoothMic) { _ in model.restartIfRunning(config(host: lastHost)) }
        .onChange(of: jitter) { value in model.engine?.setJitterMs(Int(value)) }
        .sheet(isPresented: $showManual) {
            ManualAddressSheet(initial: lastHost) { connect($0) }
        }
        .alert(item: Binding(
            get: { model.alert.map { AlertText(text: $0) } },
            set: { model.alert = $0?.text }
        )) { a in
            Alert(title: Text("SoundSwap"), message: Text(a.text))
        }
    }

    // MARK: - Sections

    private var computersSection: some View {
        Section {
            if browser.pcs.isEmpty {
                HStack(spacing: 14) {
                    ProgressView()
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Looking for PCs…")
                        Text("SoundSwap must be running on the PC, and the PC must be on the same network as the phone.")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                }
                .padding(.vertical, 6)
            } else {
                ForEach(sortedPcs) { pc in
                    Button {
                        connect(pc.address)
                    } label: {
                        HStack(spacing: 14) {
                            IconBadge(systemName: "desktopcomputer")
                            VStack(alignment: .leading, spacing: 2) {
                                Text(pc.name).font(.headline).foregroundColor(.primary)
                                Text(pc.address == lastHost ? "\(pc.address) · Last connected" : pc.address)
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                            }
                            Spacer()
                            Text("Connect").font(.subheadline.weight(.semibold)).foregroundColor(.brand)
                        }
                        .padding(.vertical, 4)
                    }
                }
            }
            Button("Enter address manually") { showManual = true }
        } header: {
            Text("PCs")
        }
    }

    private var connectionSection: some View {
        Section {
            HStack(spacing: 14) {
                let color: Color = model.hasError ? .red : (model.connected ? .green : .orange)
                ZStack {
                    Circle().fill(color.opacity(0.15)).frame(width: 38, height: 38)
                    Circle().fill(color).frame(width: 10, height: 10)
                }
                VStack(alignment: .leading, spacing: 2) {
                    Text(model.title).font(.headline).lineLimit(1)
                    Text(model.detail).font(.caption).foregroundColor(.secondary)
                    if model.connected {
                        Text(model.stats).font(.caption).foregroundColor(.secondary)
                    }
                }
            }
            .padding(.vertical, 4)
            Button(role: .destructive) {
                model.stop()
            } label: {
                Text("Disconnect").frame(maxWidth: .infinity)
            }
        } header: {
            Text("Connection")
        }
    }

    private var sortedPcs: [PcBrowser.Pc] {
        browser.pcs.sorted { a, b in
            if (a.address == lastHost) != (b.address == lastHost) { return a.address == lastHost }
            return a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending
        }
    }

    private func toggleRow(icon: String, title: String, subtitle: String, isOn: Binding<Bool>) -> some View {
        Toggle(isOn: isOn) {
            HStack(spacing: 14) {
                IconBadge(systemName: icon)
                SettingText(title: title, subtitle: subtitle)
            }
        }
        .padding(.vertical, 2)
    }

    // MARK: - Actions

    private func config(host: String) -> AudioEngine.Config {
        AudioEngine.Config(
            host: host,
            playPcAudio: play,
            sendMic: mic,
            jitterMs: Int(jitter),
            echoCancel: echoCancel,
            bluetoothMic: bluetoothMic
        )
    }

    private func connect(_ host: String) {
        let h = host.trimmingCharacters(in: .whitespaces)
        guard !h.isEmpty else { return }
        lastHost = h
        model.connect(config(host: h))
    }
}

// MARK: - Building blocks

/// SoundSwap logo — same geometry as assets/logo.svg.
struct LogoView: View {
    var size: CGFloat

    var body: some View {
        Canvas { context, canvasSize in
            let k = canvasSize.width / 32
            context.fill(Path(ellipseIn: CGRect(x: k, y: k, width: 30 * k, height: 30 * k)), with: .color(.brand))
            let bars: [(CGFloat, CGFloat, CGFloat)] = [(8, 12, 20), (12, 8, 24), (16, 10, 22), (20, 7, 25), (24, 12, 20)]
            var path = Path()
            for (x, y1, y2) in bars {
                path.move(to: CGPoint(x: x * k, y: y1 * k))
                path.addLine(to: CGPoint(x: x * k, y: y2 * k))
            }
            context.stroke(path, with: .color(.white), style: StrokeStyle(lineWidth: 2.6 * k, lineCap: .round))
        }
        .frame(width: size, height: size)
    }
}

private struct HeaderView: View {
    var body: some View {
        HStack(spacing: 14) {
            LogoView(size: 44)
            VStack(alignment: .leading, spacing: 2) {
                Text("SoundSwap").font(.title2.weight(.semibold)).foregroundColor(.primary)
                Text("PC audio on your phone, phone mic on your PC")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
        }
    }
}

private struct IconBadge: View {
    let systemName: String

    var body: some View {
        Image(systemName: systemName)
            .font(.system(size: 16, weight: .semibold))
            .foregroundColor(.brand)
            .frame(width: 34, height: 34)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color.brand.opacity(0.12)))
    }
}

private struct SettingText: View {
    let title: String
    let subtitle: String

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
            Text(subtitle).font(.caption).foregroundColor(.secondary)
        }
    }
}

private struct LevelBar: View {
    let level: Double

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.secondary.opacity(0.2))
                Capsule().fill(Color.brand).frame(width: geo.size.width * level)
            }
        }
        .frame(height: 4)
        .padding(.leading, 48)
        .padding(.vertical, 4)
    }
}

private struct ManualAddressSheet: View {
    @State private var text: String
    let onConnect: (String) -> Void
    @Environment(\.dismiss) private var dismiss

    init(initial: String, onConnect: @escaping (String) -> Void) {
        _text = State(initialValue: initial)
        self.onConnect = onConnect
    }

    var body: some View {
        NavigationView {
            Form {
                Section {
                    TextField("e.g. 192.168.1.20", text: $text)
                        .keyboardType(.numbersAndPunctuation)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                } footer: {
                    Text("Enter the IP address shown at the bottom of the SoundSwap window on your PC.")
                }
            }
            .navigationTitle("Enter address manually")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Connect") {
                        onConnect(text)
                        dismiss()
                    }
                    .disabled(text.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
    }
}

private struct AlertText: Identifiable {
    let text: String
    var id: String { text }
}
