using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SoundSwap;

sealed class AppSettings
{
    public bool SendPcAudio { get; set; } = true;
    public bool ReceiveMic { get; set; } = true;
    public int MicBufferMs { get; set; } = 30;
    public bool MutePcWhileStreaming { get; set; }
    /// <summary>null = otomatik (bulunan sanal mikrofon).</summary>
    public string? MicDeviceId { get; set; }
    public bool TrayHintShown { get; set; }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundSwap", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // "Windows ile başlat" ayarı kayıt defterinde tutulur ki uygulama taşınınca yol güncellensin.
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("SoundSwap") != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("SoundSwap", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("SoundSwap", false);
        }
    }
}
