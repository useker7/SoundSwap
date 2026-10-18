using System.Windows;

namespace SoundSwap;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Helper process launched with admin rights: installs the virtual microphone.
        if (e.Args.Length > 0 && e.Args[0] == VirtualMic.InstallArg)
        {
            Shutdown((int)VirtualMic.RunInstall(e.Args.Length > 1 ? e.Args[1] : ""));
            return;
        }

        // Helper process launched with admin rights: marks a USB phone network as private.
        if (e.Args.Length > 1 && e.Args[0] == NetworkCategory.AllowArg && Guid.TryParse(e.Args[1], out var adapterId))
        {
            Shutdown(NetworkCategory.SetPrivate(adapterId) ? 0 : 1);
            return;
        }

        string? Arg(string name) => e.Args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        string? screenshot = Arg("--screenshot");

        // Single instance: if launched again, bring the running instance's window to the front.
        // (Screenshot mode is for design checks; it leaves the running instance alone.)
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
