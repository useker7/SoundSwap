using System.Windows;

namespace SoundSwap;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Yönetici izniyle açılan yardımcı süreç: sanal mikrofon kurulumu.
        if (e.Args.Length > 0 && e.Args[0] == VirtualMic.InstallArg)
        {
            Shutdown((int)VirtualMic.RunInstall(e.Args.Length > 1 ? e.Args[1] : ""));
            return;
        }

        string? Arg(string name) => e.Args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        string? screenshot = Arg("--screenshot");

        // Tek kopya: ikinci kez açılırsa çalışan kopyanın penceresini öne getir.
        // (Ekran görüntüsü modu tasarım kontrolü içindir; çalışan kopyaya dokunmaz.)
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "SoundSwap.Show");
        if (screenshot == null)
        {
            _mutex = new Mutex(true, "SoundSwap.SingleInstance", out bool first);
            if (!first)
            {
                _showEvent.Set();
                Shutdown();
                return;
            }
        }

        Theme.Forced = Arg("--theme") switch { "dark" => true, "light" => false, _ => null };
        Theme.Initialize();

        var window = new MainWindow(startHidden: e.Args.Contains("--minimized"), screenshotPath: screenshot);

        if (screenshot != null) return;
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "show-listener" };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
