import Foundation
import Network

/// Yerel ağdaki SoundSwap bilgisayarlarını Bonjour ("_soundswap._udp") ile bulur ve IPv4 adreslerini çözer.
/// Windows uygulaması kendini bu servis adıyla duyurur. (iOS'ta UDP yayınıyla arama özel izin gerektirdiği için
/// yalnızca Bonjour kullanılır.)
final class PcBrowser: ObservableObject {
    struct Pc: Identifiable, Equatable {
        let name: String
        let address: String
        var id: String { name }
    }

    static let serviceType = "_soundswap._udp"

    @Published private(set) var pcs: [Pc] = []

    private let queue = DispatchQueue(label: "soundswap.browser")
    private var browser: NWBrowser?
    // Aşağıdakiler yalnızca `queue` üzerinde kullanılır.
    private var resolving: [String: NWConnection] = [:]
    private var resolved: Set<String> = []

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
                // Ağ değiştiğinde tarayıcı düşebilir; kısa süre sonra yeniden başlat.
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
        DispatchQueue.main.async {
            self.pcs.removeAll { !names.contains($0.name) }
        }
    }

    /// Servisi IPv4 adresine çözmek için kısa ömürlü bir UDP bağlantısı açar (veri göndermez).
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
                    DispatchQueue.main.async {
                        self.pcs.removeAll { $0.name == name }
                        self.pcs.append(Pc(name: name, address: address))
                        self.pcs.sort { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
                    }
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

    private static func ipv4String(_ host: NWEndpoint.Host) -> String? {
        guard case let .ipv4(address) = host else { return nil }
        let bytes = [UInt8](address.rawValue)
        guard bytes.count == 4 else { return nil }
        return bytes.map(String.init).joined(separator: ".")
    }
}
