import Foundation
import Network

/// Finds SoundSwap PCs on the local network via Bonjour ("_soundswap._udp") and resolves their IPv4 addresses.
/// The Windows app advertises itself under this service name. (Only Bonjour is used, because UDP broadcast
/// discovery on iOS requires a special entitlement.)
final class PcBrowser: ObservableObject {
    struct Pc: Identifiable, Equatable {
        let name: String
        let address: String
        var id: String { name }

        /// Reached through the iPhone's Personal Hotspot (over USB or Wi-Fi): 172.20.10.0/28.
        var viaHotspot: Bool { address.hasPrefix("172.20.10.") }
    }

    static let serviceType = "_soundswap._udp"

    @Published private(set) var pcs: [Pc] = []

    private let queue = DispatchQueue(label: "soundswap.browser")
    private var browser: NWBrowser?
    // The following are only used on `queue`. Keys are raw Bonjour service names.
    private var resolving: [String: NWConnection] = [:]
    private var resolved: Set<String> = []

    /// Bonjour appends "(1)", " (2)"… to a name when it sees a conflict, for example a stale registration
    /// left behind by a SoundSwap instance that was killed. They are the same PC, so the suffix is dropped.
    static func displayName(_ serviceName: String) -> String {
        guard let range = serviceName.range(of: #"\s*\(\d+\)$"#, options: .regularExpression) else { return serviceName }
        return String(serviceName[..<range.lowerBound])
    }

    func start() {
        guard browser == nil else { return }
        let params = NWParameters()
        params.includePeerToPeer = false
        let b = NWBrowser(for: .bonjour(type: Self.serviceType, domain: nil), using: params)
        b.browseResultsChangedHandler = { [weak self] results, _ in
            self?.update(results)
        }
        b.stateUpdateHandler = { [weak self] state in
            if case .failed = state {
                // The browser can fail when the network changes; restart it shortly after.
                self?.queue.asyncAfter(deadline: .now() + 2) {
                    DispatchQueue.main.async {
                        self?.stop()
                        self?.start()
                    }
                }
            }
        }
        b.start(queue: queue)
        browser = b
    }

    func stop() {
        browser?.cancel()
        browser = nil
        queue.async {
            self.resolving.values.forEach { $0.cancel() }
            self.resolving.removeAll()
            self.resolved.removeAll()
        }
        pcs = []
    }

    private func update(_ results: Set<NWBrowser.Result>) {
        var names = Set<String>()
        for result in results {
            guard case let .service(name, _, _, _) = result.endpoint else { continue }
            names.insert(name)
            if !resolved.contains(name) && resolving[name] == nil {
                resolve(name: name, endpoint: result.endpoint)
            }
        }
        for (name, connection) in resolving where !names.contains(name) {
            connection.cancel()
            resolving[name] = nil
        }
        resolved = resolved.intersection(names)
        let visible = Set(names.map(Self.displayName))
        DispatchQueue.main.async {
            self.pcs.removeAll { !visible.contains($0.name) }
        }
    }

    /// Opens a short-lived UDP connection to resolve the service to an IPv4 address (sends no data).
    private func resolve(name: String, endpoint: NWEndpoint) {
        let params = NWParameters.udp
        if let ip = params.defaultProtocolStack.internetProtocol as? NWProtocolIP.Options {
            ip.version = .v4
        }
        let connection = NWConnection(to: endpoint, using: params)
        resolving[name] = connection
        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let self, let connection else { return }
            switch state {
            case .ready:
                if case let .hostPort(host, _)? = connection.currentPath?.remoteEndpoint,
                   let address = Self.ipv4String(host) {
                    self.resolved.insert(name)
                    let pc = Pc(name: Self.displayName(name), address: address)
                    DispatchQueue.main.async { self.add(pc) }
                }
                connection.cancel()
                self.resolving[name] = nil
            case .failed, .cancelled:
                self.resolving[name] = nil
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    /// One entry per PC; a Personal Hotspot (USB) address wins over the Wi-Fi one.
    private func add(_ pc: Pc) {
        if let i = pcs.firstIndex(where: { $0.name == pc.name }) {
            if pcs[i].viaHotspot && !pc.viaHotspot { return }
            pcs[i] = pc
        } else {
            pcs.append(pc)
        }
        pcs.sort { a, b in
            if a.viaHotspot != b.viaHotspot { return a.viaHotspot }
            return a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending
        }
    }

    private static func ipv4String(_ host: NWEndpoint.Host) -> String? {
        guard case let .ipv4(address) = host else { return nil }
        let bytes = [UInt8](address.rawValue)
        guard bytes.count == 4 else { return nil }
        return bytes.map(String.init).joined(separator: ".")
    }
}
