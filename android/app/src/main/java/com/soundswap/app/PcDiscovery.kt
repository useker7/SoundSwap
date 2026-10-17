package com.soundswap.app

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.snapshots.SnapshotStateList
import java.net.Inet4Address

/**
 * Yerel ağdaki SoundSwap bilgisayarlarını bulur:
 * - Bonjour/mDNS ("_soundswap._udp"; Windows uygulaması kendini böyle duyurur)
 * - Yedek olarak UDP yayın (DISCOVER) — mDNS'in engellendiği ağlar için.
 * Liste ana iş parçacığında güncellenir; Compose doğrudan izleyebilir.
 */
class PcDiscovery(context: Context) {

    /**
     * Aynı bilgisayar birden çok yoldan/IP'den görülebilir; ada göre tek kayıt tutulur.
     * Yayına yanıt veren adres tercih edilir (telefondan ulaşılabildiği kesin).
     */
    data class Pc(
        val name: String,
        val address: String,
        val viaMdns: Boolean,
        val viaBroadcast: Boolean,
        val lastSeen: Long,
    )

    val pcs: SnapshotStateList<Pc> = mutableStateListOf()

    private val nsd = context.applicationContext.getSystemService(NsdManager::class.java)
    private val main = Handler(Looper.getMainLooper())
    private var listener: NsdManager.DiscoveryListener? = null
    private val resolveQueue = ArrayDeque<NsdServiceInfo>()
    private var resolving = false
    @Volatile private var running = false
    private var broadcastThread: Thread? = null

    fun start() {
        if (running) return
        running = true
        startMdns()
        broadcastThread = Thread({ broadcastLoop() }, "ss-discovery").apply { isDaemon = true; start() }
    }

    fun stop() {
        if (!running) return
        running = false
        listener?.let { runCatching { nsd.stopServiceDiscovery(it) } }
        listener = null
        resolveQueue.clear()
        broadcastThread?.interrupt()
        broadcastThread = null
    }

    // ---- mDNS ----

    private fun startMdns() {
        val l = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onServiceFound(info: NsdServiceInfo) {
                main.post {
                    resolveQueue.addLast(info)
                    resolveNext()
                }
            }
            override fun onServiceLost(info: NsdServiceInfo) {
                main.post {
                    val i = pcs.indexOfFirst { it.name.equals(info.serviceName, ignoreCase = true) }
                    if (i >= 0) {
                        if (pcs[i].viaBroadcast) pcs[i] = pcs[i].copy(viaMdns = false) else pcs.removeAt(i)
                    }
                }
            }
        }
        listener = l
        runCatching { nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, l) }
    }

    // Eski Android sürümleri aynı anda tek çözümlemeye izin verir; sırayla yapıyoruz.
    @Suppress("DEPRECATION")
    private fun resolveNext() {
        if (resolving || !running) return
        val info = resolveQueue.removeFirstOrNull() ?: return
        resolving = true
        val done = { resolved: NsdServiceInfo? ->
            main.post {
                resolving = false
                val address = resolved?.let { ipv4Of(it) }
                if (address != null && running) upsert(resolved.serviceName, address, viaMdns = true)
                resolveNext()
            }
        }
        try {
            nsd.resolveService(info, object : NsdManager.ResolveListener {
                override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) { done(null) }
                override fun onServiceResolved(serviceInfo: NsdServiceInfo) { done(serviceInfo) }
            })
        } catch (_: Exception) {
            done(null)
        }
    }

    @Suppress("DEPRECATION")
    private fun ipv4Of(info: NsdServiceInfo): String? {
        val addresses = if (Build.VERSION.SDK_INT >= 34) info.hostAddresses else listOfNotNull(info.host)
        return (addresses.firstOrNull { it is Inet4Address } ?: addresses.firstOrNull())?.hostAddress
    }

    // ---- UDP yayın ----

    private fun broadcastLoop() {
        while (running) {
            val found = try {
                Discovery.search(1200)
            } catch (_: Exception) {
                emptyList()
            }
            main.post {
                if (!running) return@post
                found.forEach { upsert(it.name, it.address, viaMdns = false) }
                val cutoff = SystemClock.elapsedRealtime() - 12_000
                for (i in pcs.indices.reversed()) {
                    val pc = pcs[i]
                    if (pc.viaBroadcast && pc.lastSeen < cutoff) {
                        if (pc.viaMdns) pcs[i] = pc.copy(viaBroadcast = false) else pcs.removeAt(i)
                    }
                }
            }
            try {
                Thread.sleep(3000)
            } catch (_: InterruptedException) {
                return
            }
        }
    }

    private fun upsert(name: String, address: String, viaMdns: Boolean) {
        val now = SystemClock.elapsedRealtime()
        val i = pcs.indexOfFirst { it.name.equals(name, ignoreCase = true) }
        if (i < 0) {
            pcs.add(Pc(name, address, viaMdns = viaMdns, viaBroadcast = !viaMdns, lastSeen = now))
            return
        }
        val old = pcs[i]
        pcs[i] = if (viaMdns) {
            old.copy(viaMdns = true, address = if (old.viaBroadcast) old.address else address)
        } else {
            old.copy(viaBroadcast = true, address = address, lastSeen = now)
        }
    }

    companion object {
        const val SERVICE_TYPE = "_soundswap._udp"
    }
}
