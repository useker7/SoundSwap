package com.soundswap.app

import android.content.Context
import android.content.Intent
import android.provider.Settings
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

/**
 * USB connection support. With "USB tethering" turned on, Android creates a network over the cable
 * (interface rndis0 / usb0 / ncm0) and the PC gets an address on it. SoundSwap then runs over that
 * network exactly as over Wi-Fi, but with lower and steadier latency.
 */
object UsbLink {
    private val usbInterfacePrefixes = listOf("rndis", "usb", "ncm")

    /** IPv4 address + prefix length of each active USB tethering interface on the phone. */
    fun usbSubnets(): List<Pair<Inet4Address, Int>> = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { nic -> nic.isUp && usbInterfacePrefixes.any { nic.name.startsWith(it) } }
            .flatMap { it.interfaceAddresses }
            .mapNotNull { ia -> (ia.address as? Inet4Address)?.let { it to ia.networkPrefixLength.toInt() } }
    } catch (_: Exception) {
        emptyList()
    }

    fun tetheringActive(): Boolean = usbSubnets().isNotEmpty()

    /** True if [address] (an IPv4 string) is on a USB tethering subnet, i.e. reached over the cable. */
    fun isUsbAddress(address: String): Boolean {
        val remote = try {
            InetAddress.getByName(address) as? Inet4Address
        } catch (_: Exception) {
            null
        } ?: return false
        return usbSubnets().any { (local, prefix) -> sameSubnet(local.address, remote.address, prefix) }
    }

    fun sameSubnet(a: ByteArray, b: ByteArray, prefixLength: Int): Boolean {
        if (a.size != 4 || b.size != 4) return false
        var bits = prefixLength.coerceIn(0, 32)
        for (i in 0 until 4) {
            val mask = when {
                bits >= 8 -> 0xFF
                bits <= 0 -> 0
                else -> (0xFF shl (8 - bits)) and 0xFF
            }
            if ((a[i].toInt() and mask) != (b[i].toInt() and mask)) return false
            bits -= 8
        }
        return true
    }

    /** Opens the system screen where USB tethering is switched on (falls back to general settings). */
    fun openTetheringSettings(context: Context) {
        val intents = listOf(
            Intent().setClassName("com.android.settings", "com.android.settings.TetherSettings"),
            Intent(Settings.ACTION_WIRELESS_SETTINGS),
            Intent(Settings.ACTION_SETTINGS),
        )
        for (intent in intents) {
            try {
                context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return
            } catch (_: Exception) {
            }
        }
    }
}
