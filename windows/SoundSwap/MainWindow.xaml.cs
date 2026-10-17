using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Forms = System.Windows.Forms;

namespace SoundSwap;

public partial class MainWindow : Window
{
    sealed record DeviceItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    readonly AppSettings _settings = AppSettings.Load();
    readonly MMDeviceEnumerator _enumerator = new();
    readonly AudioServer? _server;
    readonly ServiceAdvertiser? _advertiser;
    readonly DeviceWatcher? _watcher;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly DispatcherTimer _deviceDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly Forms.NotifyIcon _tray = new();
    readonly string? _screenshotPath;

    string? _sourceId;
    string? _micId;
    MMDevice? _muteDevice;
    bool? _mutedBefore;
    bool _loading = true;
    bool _exiting;
    int _tick;

    public MainWindow(bool startHidden, string? screenshotPath)
    {
        InitializeComponent();
        _screenshotPath = screenshotPath;

        try
        {
            _server = new AudioServer();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && screenshotPath != null)
        {
            // Tasarım kontrolü: başka bir kopya çalışırken de pencereyi çiz.
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            MessageBox.Show($"UDP port {Protocol.Port} is already in use by another program.",
                "SoundSwap", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }

        try { _advertiser = new ServiceAdvertiser(Protocol.Port); } catch { /* eski Windows: yalnızca yayınla bulma */ }

        _watcher = new DeviceWatcher(_enumerator);
        _watcher.DefaultOutputChanged += UpdateSource;
        _watcher.DevicesChanged += () => { _deviceDebounce.Stop(); _deviceDebounce.Start(); };
        _deviceDebounce.Tick += (_, _) =>
        {
            _deviceDebounce.Stop();
            LoadMicDevices();
            UpdateSource();
            UpdateMic();
        };

        SendSwitch.IsChecked = _settings.SendPcAudio;
        MicSwitch.IsChecked = _settings.ReceiveMic;
        MuteSwitch.IsChecked = _settings.MutePcWhileStreaming;
        StartupSwitch.IsChecked = AppSettings.StartWithWindows;
        BufferSlider.Value = Math.Clamp(_settings.MicBufferMs, 10, 150);
        BufferValue.Text = $"{(int)BufferSlider.Value} ms";
        LoadMicDevices();
        _loading = false;

        SendSwitch.Click += (_, _) => { Save(); UpdateSource(); };
        MicSwitch.Click += (_, _) => { Save(); UpdateMic(); };
        MicSetupButton.Click += OnMicSetupClick;
        MuteSwitch.Click += (_, _) => Save();
        StartupSwitch.Click += (_, _) =>
        {
            try { AppSettings.StartWithWindows = StartupSwitch.IsChecked == true; }
            catch { StartupSwitch.IsChecked = AppSettings.StartWithWindows; }
        };
        BufferSlider.ValueChanged += (_, _) =>
        {
            BufferValue.Text = $"{(int)BufferSlider.Value} ms";
            if (_server != null) _server.MicBufferMs = (int)BufferSlider.Value;
            Save();
        };
        MicDeviceCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Save();
            UpdateMic();
        };

        Footer.Text = $"This PC: {Environment.MachineName}  ·  {string.Join(", ", GetLocalIPs())}";

        UpdateSource();
        UpdateMic();
        if (_screenshotPath != null) ApplyScreenshotDemoValues();
        SetupTray();
        Theme.Changed += () => Theme.StyleTitleBar(this);
        SourceInitialized += (_, _) => Theme.StyleTitleBar(this);

        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // Tasarım kontrolü: sanal mikrofon kurulum görünümünü bu bilgisayarda da önizle.
        if (_screenshotPath != null && Environment.GetCommandLineArgs().Contains("--preview-mic-setup"))
        {
            SetMicText("A virtual microphone is needed for the phone mic to show up in apps like Discord or Zoom.", "WarnBrush");
            ShowMicSetup(MicSetup.Install);
        }
        if (_screenshotPath != null)
            ContentRendered += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, SaveScreenshot);
        if (!startHidden || _screenshotPath != null) Show();
    }

    // ---- Ses yönlendirme ----

    /// <summary>Telefona gidecek ses her zaman Windows'un o anki varsayılan çıkışından alınır.</summary>
    void UpdateSource()
    {
        string? id = null;
        string name = "No default audio output found";
        try
        {
            var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            id = dev.ID;
            name = dev.FriendlyName;
        }
        catch { }

        SourceText.Text = SendSwitch.IsChecked == true
            ? $"{name}\nCurrent Windows output, followed automatically"
            : "Off";

        string? want = SendSwitch.IsChecked == true ? id : null;
        if (_server != null && want != _sourceId)
        {
            ApplyMute(false); // eski cihazın sessiz durumunu geri ver
            _sourceId = want;
            _server.SetSource(want);
            _muteDevice?.Dispose();
            _muteDevice = id != null ? _enumerator.GetDevice(id) : null;
        }
    }

    void LoadMicDevices()
    {
        bool wasLoading = _loading;
        _loading = true;
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new DeviceItem(d.ID, d.FriendlyName))
            .OrderBy(d => d.Name)
            .ToList();
        var rec = VirtualMic.FindRecording(_enumerator);
        var items = new List<DeviceItem>
        {
            new(null, rec != null ? $"Automatic ({VirtualMic.ShortName(rec)})" : "Automatic"),
        };
        items.AddRange(devices);
        MicDeviceCombo.ItemsSource = items;
        MicDeviceCombo.SelectedItem = items.FirstOrDefault(i => i.Id != null && i.Id == _settings.MicDeviceId) ?? items[0];
        _loading = wasLoading;
    }

    enum MicSetup { None, Install, Rename }
    MicSetup _micSetup;
    bool _micSetupBusy;

    void UpdateMic()
    {
        var selected = MicDeviceCombo.SelectedItem as DeviceItem;
        var cable = VirtualMic.FindPlayback(_enumerator);
        var cableRec = VirtualMic.FindRecording(_enumerator);
        string? targetId = selected?.Id ?? cable?.ID;
        bool usingCable = targetId != null && targetId == cable?.ID;
        var setup = MicSetup.None;

        if (MicSwitch.IsChecked != true)
        {
            SetMicText("Off", "SubTextBrush");
        }
        else if (targetId == null)
        {
            SetMicText("A virtual microphone is needed for the phone mic to show up in apps like Discord or Zoom.", "WarnBrush");
            setup = MicSetup.Install;
        }
        else if (usingCable && cableRec != null)
        {
            var name = VirtualMic.ShortName(cableRec);
            SetMicText($"Select \"{name}\" as the microphone in other apps.", "SubTextBrush");
            if (name != VirtualMic.MicName) setup = MicSetup.Rename;
        }
        else
        {
            var name = (MicDeviceCombo.ItemsSource as IList<DeviceItem>)?.FirstOrDefault(i => i.Id == targetId)?.Name ?? "?";
            SetMicText($"Sending audio to \"{name}\".", "SubTextBrush");
        }
        ShowMicSetup(setup);

        string? want = MicSwitch.IsChecked == true ? targetId : null;
        if (_server != null && want != _micId)
        {
            _micId = want;
            try
            {
                _server.SetMicOutput(want, (int)BufferSlider.Value);
            }
            catch (Exception ex)
            {
                _micId = null;
                MicText.Text = "Could not open the microphone device: " + ex.Message;
                MicText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BadBrush");
            }
        }
    }

    void SetMicText(string text, string brushKey)
    {
        MicText.Text = text;
        MicText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey);
    }

    void ShowMicSetup(MicSetup setup)
    {
        if (_micSetupBusy) return;
        _micSetup = setup;
        MicSetupPanel.Visibility = setup == MicSetup.None ? Visibility.Collapsed : Visibility.Visible;
        if (setup == MicSetup.Install)
        {
            MicSetupButton.Style = (Style)FindResource("Primary");
            MicSetupButton.Content = "Install virtual microphone";
            MicSetupNote.Text = "This installs VB-Audio Virtual Cable (a free, donationware driver by VB-Audio, vb-cable.com). " +
                                "Windows will ask for administrator permission and driver approval.";
            MicSetupNote.Visibility = Visibility.Visible;
        }
        else if (setup == MicSetup.Rename)
        {
            MicSetupButton.Style = (Style)FindResource("Link");
            MicSetupButton.Content = $"Rename to \"{VirtualMic.MicName}\"";
            MicSetupNote.Visibility = Visibility.Collapsed;
        }
    }

    async void OnMicSetupClick(object sender, RoutedEventArgs e)
    {
        if (_micSetupBusy || _micSetup == MicSetup.None) return;
        _micSetupBusy = true;
        MicSetupButton.IsEnabled = false;
        var progress = new Progress<string>(s => MicSetupButton.Content = s);
        VirtualMic.Result result;
        string? error = null;
        try
        {
            result = _micSetup == MicSetup.Install
                ? await VirtualMic.InstallAsync(progress)
                : await VirtualMic.RenameAsync();
        }
        catch (Exception ex)
        {
            result = VirtualMic.Result.SetupFailed;
            error = ex.Message;
        }
        _micSetupBusy = false;
        MicSetupButton.IsEnabled = true;

        string? message = result switch
        {
            VirtualMic.Result.Ok or VirtualMic.Result.Cancelled => null,
            VirtualMic.Result.NeedsRestart => "The virtual microphone was installed. Restart your PC for it to appear.",
            VirtualMic.Result.BadSignature => "The digital signature of the downloaded file could not be verified, so the installation was stopped.",
            VirtualMic.Result.RenameFailed => "Installation finished, but the device could not be renamed.",
            _ => "The virtual microphone could not be installed." + (error != null ? "\n\n" + error : ""),
        };
        if (message != null)
            MessageBox.Show(this, message, "SoundSwap", MessageBoxButton.OK,
                result == VirtualMic.Result.NeedsRestart ? MessageBoxImage.Information : MessageBoxImage.Warning);

        LoadMicDevices();
        UpdateMic();
    }

    void ApplyMute(bool mute)
    {
        if (_muteDevice == null) return;
        try
        {
            var vol = _muteDevice.AudioEndpointVolume;
            if (mute && _mutedBefore == null)
            {
                _mutedBefore = vol.Mute;
                vol.Mute = true;
            }
            else if (!mute && _mutedBefore != null)
            {
                vol.Mute = _mutedBefore.Value;
                _mutedBefore = null;
            }
        }
        catch { }
    }

    void Save()
    {
        if (_loading) return;
        _settings.SendPcAudio = SendSwitch.IsChecked == true;
        _settings.ReceiveMic = MicSwitch.IsChecked == true;
        _settings.MutePcWhileStreaming = MuteSwitch.IsChecked == true;
        _settings.MicBufferMs = (int)BufferSlider.Value;
        _settings.MicDeviceId = (MicDeviceCombo.SelectedItem as DeviceItem)?.Id;
        _settings.Save();
    }

    // ---- Durum ----

    void Tick()
    {
        var s = _server;
        if (s == null) return;
        SendMeter.Value = Meter(s.SendPeak);
        MicMeter.Value = Meter(s.MicPeak);
        if (_tick++ % 5 != 0) return;

        ApplyMute(MuteSwitch.IsChecked == true && s.IsConnected && s.ClientWantsPcAudio && _sourceId != null);

        string state;
        if (s.SourceError != null)
        {
            state = "BadBrush";
            StatusTitle.Text = "Audio capture failed";
            StatusDetail.Text = s.SourceError;
        }
        else if (s.IsConnected)
        {
            state = "GoodBrush";
            StatusTitle.Text = s.ClientName ?? "Phone";
            var parts = new List<string> { "Connected" };
            if (s.ClientWantsPcAudio && _sourceId != null) parts.Add("sending audio");
            if (s.ClientSendsMic && _micId != null) parts.Add("receiving mic");
            StatusDetail.Text = string.Join(" · ", parts) + $"  ({s.ClientAddress})";
        }
        else
        {
            state = "WarnBrush";
            StatusTitle.Text = "Waiting for phone";
            StatusDetail.Text = "Open SoundSwap on your phone; this PC will show up in the list automatically.";
        }
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, state);
        StatusHalo.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, state);

        Stats.Text = s.IsConnected && s.ClientSendsMic
            ? $"Lost {s.MicPacketsLost} · Underruns {s.MicUnderruns}"
            : "";
        _tray.Text = s.IsConnected ? Truncate($"SoundSwap · {s.ClientName}", 63) : "SoundSwap · waiting for phone";
    }

    static double Meter(float peak)
    {
        if (peak <= 0.0001f) return 0;
        double db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    static List<string> GetLocalIPs()
    {
        var list = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = nic.GetIPProperties();
            if (props.GatewayAddresses.Count == 0 && !nic.Description.Contains("NDIS", StringComparison.OrdinalIgnoreCase))
                continue; // sanal adaptörleri (Hyper-V, VPN vb.) ele
            foreach (var addr in props.UnicastAddresses)
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add(addr.Address.ToString());
        }
        return list;
    }

    // ---- Sistem tepsisi ----

    void SetupTray()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SoundSwap.ico")).Stream;
        _tray.Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        _tray.Text = "SoundSwap";
        _tray.Visible = true;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open SoundSwap", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized) Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            // Kapat düğmesi uygulamayı kapatmaz; tepsiye küçültür. Çıkış: tepsi menüsü.
            e.Cancel = true;
            Hide();
            if (!_settings.TrayHintShown)
            {
                _tray.ShowBalloonTip(3000, "SoundSwap is running in the background",
                    "To quit completely, right-click the tray icon and choose Exit.", Forms.ToolTipIcon.None);
                _settings.TrayHintShown = true;
                _settings.Save();
            }
        }
        base.OnClosing(e);
    }

    void ExitApp()
    {
        _exiting = true;
        _timer.Stop();
        ApplyMute(false);
        _advertiser?.Dispose();
        _watcher?.Dispose();
        _server?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Ekran görüntüsü modu: bu bilgisayara özgü bilgiler (ad, IP, yerelleştirilmiş aygıt adları)
    /// paylaşılan görsellere sızmasın diye örnek değerlerle değiştirilir.
    /// </summary>
    void ApplyScreenshotDemoValues()
    {
        Footer.Text = "This PC: DESKTOP-PC  ·  192.168.1.20";
        if (SendSwitch.IsChecked == true)
            SourceText.Text = "Speakers (Realtek(R) Audio)\nCurrent Windows output, followed automatically";
        if (MicSwitch.IsChecked == true && !Environment.GetCommandLineArgs().Contains("--preview-mic-setup"))
        {
            SetMicText($"Select \"{VirtualMic.MicName}\" as the microphone in other apps.", "SubTextBrush");
            ShowMicSetup(MicSetup.None);
        }
        _loading = true;
        var demo = new DeviceItem(null, $"Automatic ({VirtualMic.MicName})");
        MicDeviceCombo.ItemsSource = new List<DeviceItem> { demo };
        MicDeviceCombo.SelectedItem = demo;
        _loading = false;
    }

    void SaveScreenshot()
    {
        const double scale = 2;
        var bmp = new RenderTargetBitmap(
            (int)(Root.ActualWidth * scale + 40 * scale), (int)(Root.ActualHeight * scale + 38 * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("BgBrush"), null, new Rect(0, 0, bmp.Width, bmp.Height));
            dc.DrawRectangle(new VisualBrush(Root), null, new Rect(20, 18, Root.ActualWidth, Root.ActualHeight));
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(_screenshotPath!)) enc.Save(fs);
        ExitApp();
    }
}
