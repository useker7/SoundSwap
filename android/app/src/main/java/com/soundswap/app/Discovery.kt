package com.soundswap.app

import android.os.SystemClock
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException

/** Yerel ağda SoundSwap çalıştıran PC'leri UDP yayınıyla bulur. Ana iş parçacığında çağırmayın. */
object Discovery {
    data class Found(val name: String, val address: String) {
        override fun toString() = "$name  ($address)"
    }

    fun search(timeoutMs: Long = 1200): List<Found> {
        val found = LinkedHashMap<String, Found>()
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = 200
            val pkt = Protocol.packet(Protocol.DISCOVER)
            val targets = broadcastAddresses() + InetAddress.getByName("255.255.255.255")
            repeat(2) {
                for (t in targets) {
                    try {
                        socket.send(DatagramPacket(pkt, pkt.size, t, Protocol.PORT))
                    } catch (_: Exception) {
                    }
                }
            }
            val buf = ByteArray(512)
            val deadline = SystemClock.elapsedRealtime() + timeoutMs
            while (SystemClock.elapsedRealtime() < deadline) {
                val p = DatagramPacket(buf, buf.size)
                try {
                    socket.receive(p)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                if (Protocol.typeOf(buf, p.length) != Protocol.DISCOVER_REPLY) continue
                val addr = p.address.hostAddress ?: continue
                val name = String(buf, Protocol.HEADER_SIZE, p.length - Protocol.HEADER_SIZE, Charsets.UTF_8)
                found[addr] = Found(name, addr)
            }
        }
        return found.values.toList()
    }

    private fun broadcastAddresses(): List<InetAddress> = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.interfaceAddresses }
            .mapNotNull { it.broadcast }
    } catch (_: Exception) {
        emptyList()
    }
}
