using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SoundSwap;

/// <summary>
/// Windows marks every newly seen network as "Public", including the network a phone creates over USB
/// (Android USB tethering, iPhone Personal Hotspot). The firewall permission SoundSwap gets on first run
/// only covers private networks, so the phone's packets are dropped on a public one.
/// This finds USB phone networks that are public and can switch one to private (needs admin rights).
/// Uses the Network List Manager COM API, the same one behind Settings > Network profile type.
/// </summary>
static class NetworkCategory
{
    public const string AllowArg = "--usb-allow";

    public sealed record UsbNetwork(Guid AdapterId, string AdapterName);

    /// <summary>USB phone networks that Windows currently treats as public.</summary>
    public static IReadOnlyList<UsbNetwork> FindPublicUsbNetworks()
    {
        var result = new List<UsbNetwork>();
        try
        {
            var usbAdapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && LinkInfo.IsUsbAdapter(n.Description))
                .ToDictionary(n => Guid.Parse(n.Id), n => n.Name);
            if (usbAdapters.Count == 0) return result;

            foreach (var (adapterId, network) in Connections())
            {
                if (usbAdapters.TryGetValue(adapterId, out var name) &&
                    network.GetCategory() == NlmNetworkCategory.Public)
                    result.Add(new UsbNetwork(adapterId, name));
            }
        }
        catch
        {
            // The check is best-effort; never let it break the UI.
        }
        return result;
    }

    /// <summary>Marks the network on the given adapter as private. Run from an elevated process.</summary>
    public static bool SetPrivate(Guid adapterId)
    {
        try
        {
            foreach (var (id, network) in Connections())
            {
                if (id != adapterId) continue;
                network.SetCategory(NlmNetworkCategory.Private);
                return network.GetCategory() == NlmNetworkCategory.Private;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Runs an elevated copy of SoundSwap that marks the network private (one UAC prompt).
    /// Returns null if the user declined the prompt.
    /// </summary>
    public static async Task<bool?> AllowAsync(Guid adapterId)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"{AllowArg} {adapterId:D}")
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        System.Diagnostics.Process? p;
        try { p = System.Diagnostics.Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        if (p == null) return false;
        await p.WaitForExitAsync();
        return p.ExitCode == 0;
    }

    static IEnumerable<(Guid AdapterId, INetwork Network)> Connections()
    {
        var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!;
        var nlm = (INetworkListManager)Activator.CreateInstance(type)!;
        var connections = nlm.GetNetworkConnections();
        var batch = new INetworkConnection[1];
        while (connections.Next(1, batch, out int fetched) == 0 && fetched == 1)
        {
            var c = batch[0];
            yield return (c.GetAdapterId(), c.GetNetwork());
        }
    }

    // ---- Network List Manager COM interfaces (netlistmgr.h). Only the vtable slots used here are declared,
    // in their original order; trailing methods may be omitted.

    enum NlmNetworkCategory
    {
        Public = 0,
        Private = 1,
        DomainAuthenticated = 2,
    }

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] INetwork GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object get__NewEnum();

        [PreserveSig]
        int Next(int celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INetworkConnection[] elements,
            out int fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetworkConnection
    {
        INetwork GetNetwork();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnected();
        int GetConnectivity();
        NlmNetworkCategory GetCategory();
        void SetCategory(NlmNetworkCategory category);
    }
}
