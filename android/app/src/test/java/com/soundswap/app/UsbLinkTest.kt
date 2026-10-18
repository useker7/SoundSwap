package com.soundswap.app

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UsbLinkTest {
    private fun ip(s: String) = s.split('.').map { it.toInt().toByte() }.toByteArray()

    @Test
    fun sameSubnet_matchesTetheringNetwork() {
        // Android USB tethering: phone 192.168.42.129/24, PC 192.168.42.10
        assertTrue(UsbLink.sameSubnet(ip("192.168.42.129"), ip("192.168.42.10"), 24))
    }

    @Test
    fun sameSubnet_rejectsWifiAddress() {
        assertFalse(UsbLink.sameSubnet(ip("192.168.42.129"), ip("192.168.1.20"), 24))
    }

    @Test
    fun sameSubnet_handlesNonOctetPrefix() {
        assertTrue(UsbLink.sameSubnet(ip("172.20.10.1"), ip("172.20.10.14"), 28))
        assertFalse(UsbLink.sameSubnet(ip("172.20.10.1"), ip("172.20.10.17"), 28))
    }

    @Test
    fun sameSubnet_zeroPrefixMatchesEverything() {
        assertTrue(UsbLink.sameSubnet(ip("10.0.0.1"), ip("192.168.1.1"), 0))
    }
}
