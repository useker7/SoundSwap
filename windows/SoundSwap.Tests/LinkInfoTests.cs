using System.Net;

namespace SoundSwap.Tests;

public class LinkInfoTests
{
    [Theory]
    [InlineData("Remote NDIS based Internet Sharing Device", true)]          // Android USB tethering
    [InlineData("Remote NDIS Compatible Device #2", true)]
    [InlineData("Apple Mobile Device Ethernet", true)]                        // iPhone Personal Hotspot over USB
    [InlineData("UsbNcm Host Device", true)]
    [InlineData("Intel(R) Wi-Fi 6E AX211 160MHz", false)]
    [InlineData("Realtek PCIe GbE Family Controller", false)]
    [InlineData("Hyper-V Virtual Ethernet Adapter", false)]
    public void IsUsbAdapter_RecognisesPhoneTetheringAdapters(string description, bool expected)
    {
        Assert.Equal(expected, LinkInfo.IsUsbAdapter(description));
    }

    [Theory]
    [InlineData("192.168.42.10", "192.168.42.129", "255.255.255.0", true)]
    [InlineData("192.168.42.10", "192.168.1.20", "255.255.255.0", false)]
    [InlineData("172.20.10.2", "172.20.10.1", "255.255.255.240", true)]
    [InlineData("172.20.10.2", "172.20.10.17", "255.255.255.240", false)]
    public void SameSubnet(string a, string b, string mask, bool expected)
    {
        Assert.Equal(expected, LinkInfo.SameSubnet(IPAddress.Parse(a), IPAddress.Parse(b), IPAddress.Parse(mask)));
    }
}
