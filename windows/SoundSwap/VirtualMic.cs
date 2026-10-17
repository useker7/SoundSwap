using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SoundSwap;

/// <summary>
/// Virtual microphone: VB-Audio Virtual Cable (donationware, vb-cable.com).
/// SoundSwap plays the phone audio into the cable's "playback" end; other programs pick the "recording" end as a microphone.
/// Installing the driver needs admin rights, so a separate, elevated SoundSwap process does it.
/// </summary>
static class VirtualMic
{
    public const string CableInterfaceName = "VB-Audio Virtual Cable";
    public const string MicName = "SoundSwap Microphone";
    public const string FeedName = "SoundSwap Microphone Feed";
    const string DownloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
    const string TrustedSigner = "BUREL VINCENT"; // VB-Audio Software's code-signing certificate

    public const string InstallArg = "--vmic-install";

    public enum Result { Ok = 0, Cancelled = 1, SetupFailed = 2, NeedsRestart = 3, RenameFailed = 4, BadSignature = 5 }

    static bool IsCable(MMDevice d)
    {
        try { return string.Equals(d.DeviceFriendlyName, CableInterfaceName, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>The cable's stereo input (the multichannel "CABLE In 16ch" endpoint is not preferred).</summary>
    public static MMDevice? FindPlayback(MMDeviceEnumerator en) =>
        en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(IsCable)
            .OrderBy(d => ShortName(d).Contains("16ch", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

    public static MMDevice? FindRecording(MMDeviceEnumerator en) =>
        en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(IsCable);

    /// <summary>Short name shown in Windows (e.g. "CABLE Output" or "SoundSwap Microphone").</summary>
    public static string ShortName(MMDevice d)
    {
        try { return d.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value as string ?? d.FriendlyName; }
        catch { return d.FriendlyName; }
    }

    public static bool IsRenamed(MMDeviceEnumerator en) =>
        FindRecording(en) is { } r && ShortName(r) == MicName;

    // ---- Main (non-elevated) process side ----

    /// <summary>Downloads the driver and installs it via the elevated helper process.</summary>
    public static async Task<Result> InstallAsync(IProgress<string> progress)
    {
        string dir = Path.Combine(Path.GetTempPath(), "SoundSwap", "vbcable");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        progress.Report("Downloading…");
        string zip = Path.Combine(dir, "pack.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SoundSwap/1.1");
            var bytes = await http.GetByteArrayAsync(DownloadUrl);
            await File.WriteAllBytesAsync(zip, bytes);
        }
        ZipFile.ExtractToDirectory(zip, dir);

        progress.Report("Installing…");
        return await RunElevatedAsync($"{InstallArg} \"{dir}\"");
    }

    /// <summary>Renaming needs no admin rights (same as "Rename" in Sound settings).</summary>
    public static Task<Result> RenameAsync() => Task.Run(RunRename);

    static async Task<Result> RunElevatedAsync(string args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" };
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return Result.Cancelled; } // UAC declined
        if (p == null) return Result.SetupFailed;
        await p.WaitForExitAsync();
        return (Result)p.ExitCode;
    }

    // ---- Elevated helper process side ----

    public static Result RunInstall(string dir)
    {
        string setup = Path.Combine(dir, Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe");
        if (!File.Exists(setup)) return Result.SetupFailed;
        if (!IsTrusted(setup)) return Result.BadSignature;

        // -i: install, -h: hide UI. Windows still shows the driver approval dialog.
        using (var p = Process.Start(new ProcessStartInfo(setup, "-i -h") { UseShellExecute = false, WorkingDirectory = dir }))
        {
            if (p == null || !p.WaitForExit(TimeSpan.FromMinutes(3))) return Result.SetupFailed;
        }

        // The endpoints can take a few seconds to appear in Windows.
        for (int i = 0; i < 40; i++)
        {
            using var en = new MMDeviceEnumerator();
            if (FindPlayback(en) != null && FindRecording(en) != null) return RunRename();
            Thread.Sleep(500);
        }
        return Result.NeedsRestart;
    }

    public static Result RunRename()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var rec = FindRecording(en);
            var play = FindPlayback(en);
            if (rec == null || play == null) return Result.RenameFailed;
            SetShortName(rec, MicName);
            SetShortName(play, FeedName);
            return Result.Ok;
        }
        catch
        {
            return Result.RenameFailed;
        }
    }

    /// <summary>Writes the same property as "Rename" in Windows Sound settings (PKEY_Device_DeviceDesc).</summary>
    static void SetShortName(MMDevice device, string name)
    {
        device.GetPropertyInformation(StorageAccessMode.ReadWrite);
        var value = new PropVariant { vt = (short)VarEnum.VT_LPWSTR, pointerValue = Marshal.StringToCoTaskMemUni(name) };
        try
        {
            device.Properties.SetValue(PropertyKeys.PKEY_Device_DeviceDesc, value);
            device.Properties.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.pointerValue);
        }
    }

    /// <summary>Is the file's Authenticode signature valid and does it belong to VB-Audio?</summary>
    static bool IsTrusted(string path)
    {
        if (WinVerifyTrustFile(path) != 0) return false;
        try
        {
#pragma warning disable SYSLIB0057
            var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains(TrustedSigner, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---- WinVerifyTrust ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WinTrustFileInfo
    {
        public int StructSize;
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WinTrustData
    {
        public int StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public int UIChoice;
        public int RevocationChecks;
        public int UnionChoice;
        public IntPtr FileInfo;
        public int StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public int ProvFlags;
        public int UIContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

    static int WinVerifyTrustFile(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var file = new WinTrustFileInfo { StructSize = Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WinTrustData
            {
                StructSize = Marshal.SizeOf<WinTrustData>(),
                UIChoice = 2,         // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE
                UnionChoice = 1,      // WTD_CHOICE_FILE
                FileInfo = pFile,
                StateAction = 0,
            };
            return WinVerifyTrust(IntPtr.Zero, action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
