package com.soundswap.app

import org.junit.Assert.assertEquals
import org.junit.Test

class PcDiscoveryTest {
    @Test
    fun displayName_dropsBonjourConflictSuffix() {
        assertEquals("086F1EAA", PcDiscovery.displayName("086F1EAA(1)"))
        assertEquals("Gaming PC", PcDiscovery.displayName("Gaming PC (2)"))
    }

    @Test
    fun displayName_keepsOrdinaryNames() {
        assertEquals("DESKTOP-ABC", PcDiscovery.displayName("DESKTOP-ABC"))
        assertEquals("PC (work)", PcDiscovery.displayName("PC (work)"))
    }
}
