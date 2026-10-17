using System.Net;
using System.Runtime.InteropServices;

namespace SoundSwap;

/// <summary>
/// Advertises this PC on the local network as the "_soundswap._udp" service via DNS-SD/mDNS ("Bonjour").
/// This lets iOS (NWBrowser) and Android (NsdManager) find the PC without typing an IP.
/// Uses the mDNS responder built into Windows 10 1903+ (dnsapi.dll).
/// </summary>
sealed class ServiceAdvertiser : IDisposable
{
    public const string ServiceType = "_soundswap._udp";
    const uint DnsRequestPending = 9506;

    [StructLayout(LayoutKind.Sequential)]
    struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr CompletionCallback;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DnsServiceConstructInstance(
        string serviceName, string hostName, IntPtr ip4, IntPtr ip6,
        ushort port, ushort priority, ushort weight, uint propertiesCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);

    [DllImport("dnsapi.dll")] static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);

    readonly RegisterComplete _callback; // kept in a field so the GC doesn't collect it
    IntPtr _request;

    /// <summary>0 = success, null = no reply yet, other = Windows error code.</summary>
    public uint? Status { get; private set; }

    public ServiceAdvertiser(ushort port)
    {
        _callback = (status, _, _) => Status = status;
        string instance = Sanitize(Environment.MachineName);
        string host = Dns.GetHostName() + ".local";
        IntPtr inst = DnsServiceConstructInstance(
            $"{instance}.{ServiceType}.local", host, IntPtr.Zero, IntPtr.Zero,
            port, 0, 0, 1, ["v"], [Protocol.Version.ToString()]);
        if (inst == IntPtr.Zero)
            throw new InvalidOperationException("DnsServiceConstructInstance failed");

        var req = new DnsServiceRegisterRequest
        {
            Version = 1, // DNS_QUERY_REQUEST_VERSION1
            ServiceInstance = inst,
            CompletionCallback = Marshal.GetFunctionPointerForDelegate(_callback),
        };
        _request = Marshal.AllocHGlobal(Marshal.SizeOf<DnsServiceRegisterRequest>());
        Marshal.StructureToPtr(req, _request, false);
        uint r = DnsServiceRegister(_request, IntPtr.Zero);
        if (r != DnsRequestPending)
            throw new InvalidOperationException($"DnsServiceRegister: {r}");
    }

    static string Sanitize(string name)
    {
        name = name.Replace('.', '-');
        return name.Length > 60 ? name[..60] : name;
    }

    public void Dispose()
    {
        if (_request == IntPtr.Zero) return;
        // The registration is also removed automatically when the process dies; here we remove it explicitly.
        // The request block is not freed because the completion callback may still use it.
        DnsServiceDeRegister(_request, IntPtr.Zero);
        _request = IntPtr.Zero;
    }
}
