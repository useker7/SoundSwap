import Darwin

/// Thin BSD UDP socket talking to a single peer (the PC). Uses system calls directly for low latency.
final class UDPSocket {
    private let fd: Int32
    private var dest = sockaddr_in()

    /// `host` must be an IPv4 address (e.g. 192.168.1.20).
    init?(host: String, port: UInt16) {
        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        guard inet_pton(AF_INET, host, &addr.sin_addr) == 1 else { return nil }

        let s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard s >= 0 else { return nil }
        fd = s
        dest = addr

        var timeout = timeval(tv_sec: 0, tv_usec: 300_000)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        var bufSize: Int32 = 256 * 1024
        setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &bufSize, socklen_t(MemoryLayout<Int32>.size))
        var tos: Int32 = 0xB8 // DSCP EF: voice priority on Wi-Fi
        setsockopt(fd, IPPROTO_IP, IP_TOS, &tos, socklen_t(MemoryLayout<Int32>.size))
        var noSigPipe: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))
    }

    deinit {
        Darwin.close(fd)
    }

    @discardableResult
    func send(_ bytes: UnsafeRawPointer, count: Int) -> Bool {
        var addr = dest
        let sent = withUnsafePointer(to: &addr) { p in
            p.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                sendto(fd, bytes, count, 0, sa, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        return sent == count
    }

    func send(_ bytes: [UInt8]) {
        bytes.withUnsafeBytes { raw in
            _ = send(raw.baseAddress!, count: raw.count)
        }
    }

    /// Waits at most 300 ms; returns -1 on timeout or error.
    func receive(into buffer: UnsafeMutableRawPointer, capacity: Int) -> Int {
        recv(fd, buffer, capacity, 0)
    }
}
