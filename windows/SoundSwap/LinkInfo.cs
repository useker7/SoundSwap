using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SoundSwap;

/// <summary>
/// Tells whether a phone is reached over a USB cable. Both Android ("USB tethering") and iPhone
/// ("Personal Hotspot" over USB) appear in Windows as a USB network adapter whose subnet contains the phone.
/// </summary>
static class LinkInfo
{
    static readonly string[] UsbAdapterHints =
    [
        "Remote NDIS", "RNDIS", "Apple Mobile Device Ethernet", "NCM", "USB Ethernet", "Android USB",
    ];

    public static bool IsUsbAdapter(string description) =>
        UsbAdapterHints.Any(h => description.Contains(h, StringComparison.OrdinalIgnoreCase));

    public static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        byte[] x = a.GetAddressBytes(), y = b.GetAddressBytes(), m = mask.GetAddressBytes();
        if (x.Length != 4 || y.Length != 4 || m.Length != 4) return false;
        for (int i = 0; i < 4; i++)
            if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }

    /// <summary>True if <paramref name="remote"/> is on the subnet of a USB network adapter of this PC.</summary>
    public static bool IsViaUsb(IPAddress remote)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || !IsUsbAdapter(nic.Description)) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        SameSubnet(ua.Address, remote, ua.IPv4Mask))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }
}
