using System.Net;
using System.Runtime.InteropServices;

namespace SoundSwap;

/// <summary>
/// Bu bilgisayarı yerel ağda DNS-SD/mDNS ("Bonjour") ile "_soundswap._udp" servisi olarak duyurur.
/// iOS (NWBrowser) ve Android (NsdManager) bu sayede PC'yi IP yazmadan bulur.
/// Windows 10 1903+ içindeki yerleşik mDNS yanıtlayıcısını kullanır (dnsapi.dll).
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

    readonly RegisterComplete _callback; // GC'den korumak için alan olarak tutulur
    IntPtr _request;

    /// <summary>0 = başarılı, null = henüz yanıt yok, diğer = Windows hata kodu.</summary>
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
        // Kayıt süreç ölünce de otomatik silinir; burada açıkça kaldırıyoruz.
        // İstek bloğu tamamlanma geri çağrısında kullanılabileceği için serbest bırakılmaz.
        DnsServiceDeRegister(_request, IntPtr.Zero);
        _request = IntPtr.Zero;
    }
}
