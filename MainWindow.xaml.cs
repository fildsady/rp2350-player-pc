using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Midi;
using IOPath = System.IO.Path;

namespace PicoAudioCore;

public partial class MainWindow : Window
{
    private readonly SerialService   _serial      = new();
    private readonly DispatcherTimer _clockTimer     = new();
    private readonly DispatcherTimer _beatTimer      = new();
    private readonly DispatcherTimer _reconnectTimer = new();

    private bool   _connected    = false;
    private bool   _paused       = false;
    private string _repeatMode   = "";
    private int    _currentTrack = 0;   /* 1-based, from STATUS track= */
    private int    _totalTracks  = 0;   /* from STATUS total= */
    private static readonly Random _rng = new();
    private bool   _autoPlay    = true;
    private bool   _volumeSync  = false;
    private bool   _eqSync      = false;
    private bool   _mono        = false;
    private bool   _uploading   = false;
    private const int UploadChunk = 512;

    private const int EqBands = 10;
    private static readonly float[] EqFreqs = {
        31.5f, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000
    };
    private readonly Slider[]    _eqSliders  = new Slider[EqBands];
    private readonly TextBlock[] _eqDbLabels = new TextBlock[EqBands];

    private const int HotkeyCount = 24;
    private readonly TextBox[] _hkBoxes = new TextBox[HotkeyCount];
    private static readonly string HotkeyFile =
        IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotkeys.json");
    private static readonly string LastPortFile =
        IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "lastport.txt");
    private static readonly string WindowStateFile =
        IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "windowstate.json");
    private static readonly string SettingsFile =
        IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    private bool _autoConnect = false;

    // ── MIDI CUE ──────────────────────────────────────────────────────────────
    private class MidiCueEntry
    {
        public int    Note    { get; set; } = 60;
        public string Command { get; set; } = "goto";
        public string Track   { get; set; } = "";
    }
    private readonly List<MidiCueEntry> _midiCues = new();
    private MidiIn? _midiIn = null;
    private static readonly string MidiCueFile =
        IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "midicues.json");

    private static string NoteName(int n)
    {
        string[] names = { "C","C#","D","D#","E","F","F#","G","G#","A","A#","B" };
        return $"{names[n % 12]}{n / 12 - 2}";
    }

    private const string StartupRegKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupAppName = "PicoAudioCore";

    private static bool IsStartupEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegKey);
        return key?.GetValue(StartupAppName) != null;
    }

    private static void SetStartup(bool enable)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegKey, writable: true);
        if (key == null) return;
        if (enable)
            key.SetValue(StartupAppName, $"\"{System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName}\"");
        else
            key.DeleteValue(StartupAppName, throwOnMissingValue: false);
    }

    // ── Schedule ──────────────────────────────────────────────────────────────
    private class ScheduleEntry
    {
        public bool   Enabled  { get; set; } = true;
        public string Time     { get; set; } = "08:00";
        public string StopTime { get; set; } = "";      /* "" = no stop time */
        public string Tracks   { get; set; } = "";     /* comma-separated, e.g. "song1,song2" */
        public int    Loops    { get; set; } = 1;       /* 0 = infinite */
        public bool[] Days     { get; set; } = new bool[7] { true,true,true,true,true,true,true };
    }
    private readonly List<ScheduleEntry> _schedules          = new();

    /* ── Schedule mode ─────────────────────────────────────────────────────── */
    private enum SchedMode { PicoScheduler, GuiScheduler }
    private SchedMode _schedMode = SchedMode.PicoScheduler;

    /* GUI-scheduler runtime state */
    private string[]  _guiPlaylist     = Array.Empty<string>();
    private int       _guiPlPos        = 0;
    private int       _guiLoopsRemain  = 0;   /* -1=infinite, 0=idle, >0=countdown */
    private string    _guiActiveTrack  = "";
    private string    _lastSchedMinute = "";
    private ScheduleEntry? _guiActiveEntry = null;

    /* kept for Pico-loop tracking (single-track backward compat) */
    private string _schedFile           = "";
    private int    _schedLoopsRemaining = 0;
    private string _lastState           = "";
    private static readonly string[] DayAbbr = { "Mo","Tu","We","Th","Fr","Sa","Su" };
    private static readonly string ScheduleFile =
        IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "schedules.json");

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowState();
        LoadSettings();
        BuildHotkeySlots();
        LoadHotkeys();
        BuildEqSliders();
        LoadSchedules();
        LoadMidiCues();
        RefreshMidiPorts();
        Loaded += (_, _) => { if (_autoConnect) TryAutoConnect(); };

        _serial.LineReceived += OnLineReceived;
        _serial.Disconnected += () => Dispatcher.Invoke(() =>
        {
            Log("[ERR] Board disconnected");
            SetConnected(false);
            if (_autoConnect)
            {
                Log("[AUTO] Reconnecting…");
                _reconnectTimer.Start();
            }
        });

        _reconnectTimer.Interval = TimeSpan.FromSeconds(3);
        _reconnectTimer.Tick += (_, _) =>
        {
            if (_connected) { _reconnectTimer.Stop(); return; }
            if (_serial.Open())
            {
                _reconnectTimer.Stop();
                SetConnected(true);
                _beatTimer.Start();
                _serial.Send("version");
                Log("[AUTO] Reconnected (HID)");
            }
        };

        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) =>
        {
            TbPcTime.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
            CheckSchedule();
        };
        _clockTimer.Start();

        _beatTimer.Interval = TimeSpan.FromSeconds(1);
        _beatTimer.Tick += (_, _) =>
        {
            if (_connected) _serial.Send("status");
        };
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) { /* HID: no ports to refresh */ }

    // ── Signal Generator ──────────────────────────────────────────────────────

    private string GetSelectedWaveform()
    {
        foreach (var rb in new[] { RbSine, RbSquare, RbTriangle, RbSaw, RbWhite, RbPink })
            if (rb.IsChecked == true) return (string)rb.Tag;
        return "sine";
    }

    private void BtnSigStart_Click(object sender, RoutedEventArgs e)
    {
        string wave = GetSelectedWaveform();
        string cmd;
        if (wave == "white" || wave == "pink")
            cmd = $"siggen {wave}";
        else
        {
            if (!float.TryParse(TbSigFreq.Text, out float freq) || freq < 1 || freq > 20000)
            {
                Log("[SIGGEN] ความถี่ต้องเป็น 1–20000 Hz");
                return;
            }
            cmd = $"siggen {wave} {(int)freq}";
        }
        _serial.Send(cmd);
        TbSigStatus.Text = $"Running:  {wave}  {(wave is "white" or "pink" ? "" : TbSigFreq.Text + " Hz")}".TrimEnd();
        SigGenStatusBar.Visibility = Visibility.Visible;
        Log($"[SIGGEN] {cmd}");
    }

    private void BtnSigStop_Click(object sender, RoutedEventArgs e)
    {
        _serial.Send("siggen off");
        SigGenStatusBar.Visibility = Visibility.Collapsed;
        Log("[SIGGEN] off");
    }

    private bool _sigFreqUpdating = false;

    private void BtnSigFreqPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            _sigFreqUpdating = true;
            TbSigFreq.Text = (string)btn.Tag;
            if (double.TryParse((string)btn.Tag, out var f)) SlSigFreq.Value = f;
            _sigFreqUpdating = false;
        }
    }

    private void SlSigFreq_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sigFreqUpdating || TbSigFreq == null) return;
        int hz = (int)SlSigFreq.Value;
        _sigFreqUpdating = true;
        TbSigFreq.Text = hz.ToString();
        _sigFreqUpdating = false;
        SendSigFreqLive(hz);
    }

    private void TbSigFreq_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_sigFreqUpdating || SlSigFreq == null) return;
        if (int.TryParse(TbSigFreq.Text, out var hz) && hz >= 1 && hz <= 20000)
        {
            _sigFreqUpdating = true;
            SlSigFreq.Value = hz;
            _sigFreqUpdating = false;
            SendSigFreqLive(hz);
        }
    }

    private void SendSigFreqLive(int hz)
    {
        if (!_connected || SigGenStatusBar.Visibility != Visibility.Visible) return;
        _serial.Send($"sigfreq {hz}");
        TbSigStatus.Text = $"Running:  {GetSelectedWaveform()}  {hz} Hz";
    }

    private void SlSigVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TbSigVolPct == null) return;
        int v = (int)SlSigVolume.Value;
        TbSigVolPct.Text = $"{v}%";
        string dbStr = v == 0 ? "−∞ dBFS" : $"{20.0 * Math.Log10(v / 100.0):0.0} dBFS";
        TbSigVolDb.Text = dbStr;
        // sync with Player tab slider and send to board
        if (SlVolume != null) SlVolume.Value = v;
        if (_connected && _volumeSync) _serial.Send($"volume {v}");
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (TbMaxIcon == null) return;
        TbMaxIcon.Text = WindowState == WindowState.Maximized ? "⧅" : "□";
    }

    private void LoadSettings()
    {
        if (!File.Exists(SettingsFile)) return;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsFile));
            if (doc.RootElement.TryGetProperty("autoconnect", out var ac))
                _autoConnect = ac.GetBoolean();
        }
        catch { }
        if (TgAutoConnect != null)
            TgAutoConnect.IsChecked = _autoConnect;
        if (TgStartup != null)
            TgStartup.IsChecked = IsStartupEnabled();
    }

    private void SaveSettings()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            autoconnect = _autoConnect,
            repeat      = _repeatMode,
        });
        File.WriteAllText(SettingsFile, json);
    }

    private void TryAutoConnect()
    {
        if (_serial.Open())
        {
            SetConnected(true);
            _beatTimer.Start();
            _serial.Send("version");
            _serial.Send("status");
            Log("[AUTO] Connected (HID)");
        }
        else
            Log("[AUTO] PicoAudioCore HID device not found");
    }

    private void TgAutoConnect_Click(object sender, RoutedEventArgs e)
    {
        _autoConnect = TgAutoConnect.IsChecked == true;
        SaveSettings();
    }

    private void TgStartup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStartup(TgStartup.IsChecked == true);
        }
        catch (Exception ex)
        {
            TgStartup.IsChecked = IsStartupEnabled();
            Log($"[ERR] Startup registry: {ex.Message}");
        }
    }

    private void RefreshPorts() { /* HID: auto-discovered by VID/PID */ }

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_connected)
        {
            _reconnectTimer.Stop();
            _beatTimer.Stop();
            _serial.Close();
            SetConnected(false);
            return;
        }
        if (_serial.Open())
        {
            SetConnected(true);
            _beatTimer.Start();
            _serial.Send("version");
            _serial.Send("status");
        }
        else
            Log("[ERR] PicoAudioCore HID device not found — plug in Pico");
    }

    private void SetConnected(bool on)
    {
        _connected = on;
        if (!on) { _beatTimer.Stop(); TbFirmware.Text = ""; _volumeSync = false; _eqSync = false; _mono = false; _repeatMode = ""; UpdateMonoToggleUI(false); UpdateModeButtons(); }
        BtnConnect.Content        = on ? "Disconnect" : "Connect";
        StatusDot.Fill            = on ? Brushes.LightGreen : new SolidColorBrush(Color.FromRgb(0xf3,0x8b,0xa8));
        BtnPrev.IsEnabled         = on;
        BtnPlay.IsEnabled         = on;
        BtnPause.IsEnabled        = on;
        BtnStop.IsEnabled         = on;
        BtnNext.IsEnabled         = on;
        BtnQPlay.IsEnabled        = on;
        BtnQPause.IsEnabled       = on;
        BtnQStop.IsEnabled        = on;
        BtnGoto.IsEnabled         = on;
        BtnSyncTime.IsEnabled     = on;
        SlVolume.IsEnabled        = on;
        BtnRepeatOne.IsEnabled    = on;
        BtnRepeatAll.IsEnabled    = on;
        BtnRepeatOff.IsEnabled    = on;
        BtnRepeatSingle.IsEnabled = on;
        BtnRepeatRandom.IsEnabled = on;
        BtnAutoPlay.IsEnabled     = on;
        BtnEqReset.IsEnabled      = on;
        foreach (var s in _eqSliders) if (s != null) s.IsEnabled = on;
        MonoToggleBorder.IsEnabled = on;
        BtnSendFile.IsEnabled      = on && !_uploading;
        BtnSigStart.IsEnabled      = on;
        BtnSigStop.IsEnabled       = on;
        UpdateSchedModeUI();
        if (!on)
        {
            TbNowPlaying.Text = "— Not connected —"; TbElapsed.Text = "--:--"; TbTrackInfo.Text = "";
            _guiLoopsRemain = 0; _guiActiveTrack = ""; _guiActiveEntry = null;
        }
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_repeatMode == "rnd" && _totalTracks > 1)
            _serial.Send($"goto {RandomTrackNum()}");
        else
            _serial.Send("prev");
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_repeatMode == "rnd" && _totalTracks > 1)
            _serial.Send($"goto {RandomTrackNum()}");
        else
            _serial.Send("next");
    }

    private int RandomTrackNum()
    {
        int next;
        do { next = _rng.Next(1, _totalTracks + 1); } while (next == _currentTrack);
        return next;
    }
    private void BtnStop_Click (object sender, RoutedEventArgs e) => _serial.Send("stop");

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        _paused = false;
        BtnPause.Content = "⏸  PAUSE";
        _serial.Send("play");
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        BtnPause.Content = _paused ? "▶  RESUME" : "⏸  PAUSE";
        _serial.Send(_paused ? "pause" : "play");
    }

    private void BtnGoto_Click(object sender, RoutedEventArgs e) => SendGoto();
    private void TbGoto_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SendGoto(); }

    private void SendGoto()
    {
        var name = TbGoto.Text.Trim();
        if (string.IsNullOrEmpty(name) || !_connected) return;
        _serial.Send($"goto {name}");
    }

    // ── Playback mode ─────────────────────────────────────────────────────────

    private void SetRepeatMode(string mode, string cmd)
    {
        _repeatMode = mode; SaveSettings(); _serial.Send(cmd); UpdateModeButtons();
    }
    private void BtnRepeatOne_Click   (object sender, RoutedEventArgs e) => SetRepeatMode("one",    "mode repeat_one");
    private void BtnRepeatAll_Click   (object sender, RoutedEventArgs e) => SetRepeatMode("all",    "mode repeat_all");
    private void BtnRepeatOff_Click   (object sender, RoutedEventArgs e) => SetRepeatMode("off",    "mode repeat_off");
    private void BtnRepeatSingle_Click(object sender, RoutedEventArgs e) => SetRepeatMode("single", "mode repeat_single");
    private void BtnRepeatRandom_Click(object sender, RoutedEventArgs e) => SetRepeatMode("rnd",    "mode repeat_random");
    private void BtnAutoPlay_Click    (object sender, RoutedEventArgs e) =>
        _serial.Send(_autoPlay ? "mode autoplay_off" : "mode autoplay_on");

    private static readonly SolidColorBrush BrushActive   = new(Color.FromRgb(0x7a, 0xa2, 0xf7));
    private static readonly SolidColorBrush BrushInactive = new(Color.FromRgb(0x18, 0x18, 0x2a));

    private void UpdateModeButtons()
    {
        BtnRepeatOne.Background    = _repeatMode == "one"    ? BrushActive : BrushInactive;
        BtnRepeatAll.Background    = _repeatMode == "all"    ? BrushActive : BrushInactive;
        BtnRepeatOff.Background    = _repeatMode == "off"    ? BrushActive : BrushInactive;
        BtnRepeatSingle.Background = _repeatMode == "single" ? BrushActive : BrushInactive;
        BtnRepeatRandom.Background = _repeatMode == "rnd"    ? BrushActive : BrushInactive;
        var dark = new SolidColorBrush(Color.FromRgb(0x0d, 0x14, 0x30));
        BtnRepeatOne.Foreground    = _repeatMode == "one"    ? dark : Brushes.White;
        BtnRepeatAll.Foreground    = _repeatMode == "all"    ? dark : Brushes.White;
        BtnRepeatOff.Foreground    = _repeatMode == "off"    ? dark : Brushes.White;
        BtnRepeatSingle.Foreground = _repeatMode == "single" ? dark : Brushes.White;
        BtnRepeatRandom.Foreground = _repeatMode == "rnd"    ? dark : Brushes.White;

        BtnAutoPlay.Background = _autoPlay ? BrushActive : BrushInactive;
        BtnAutoPlay.Foreground = _autoPlay ? new SolidColorBrush(Color.FromRgb(0x0d,0x14,0x30)) : Brushes.White;
        BtnAutoPlay.Content    = _autoPlay ? "▶  Auto On" : "⏸  Auto Off";
    }

    // ── Time sync ─────────────────────────────────────────────────────────────

    private void BtnSyncTime_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _serial.Send($"date {now:yyyy-MM-dd HH:mm:ss}");
        Log($"[PC] Synced → {now:yyyy-MM-dd HH:mm:ss}");
    }

    // ── Hotkeys ───────────────────────────────────────────────────────────────

    private void BuildHotkeySlots()
    {
        var hkBtnStyle = (Style)FindResource("HkPlayBtn");
        for (int i = 0; i < HotkeyCount; i++)
        {
            int idx = i;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

            var label = new TextBlock
            {
                Text      = $"{i + 1:D2}",
                Foreground = new SolidColorBrush(Color.FromRgb(0xf9, 0xc7, 0x4f)),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 10, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);

            var tb = new TextBox
            {
                Background      = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x14)),
                Foreground      = new SolidColorBrush(Color.FromRgb(0xb0, 0xc8, 0xff)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x42)),
                CaretBrush      = new SolidColorBrush(Color.FromRgb(0x7a, 0xaa, 0xee)),
                SelectionBrush  = new SolidColorBrush(Color.FromRgb(0x1e, 0x3a, 0x6a)),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(4, 2, 4, 2),
                FontFamily      = new FontFamily("Consolas"),
                FontSize        = 10,
                ToolTip         = $"Hotkey {i + 1}: filename without extension"
            };
            tb.TextChanged += (_, _) => SaveHotkeys();
            Grid.SetColumn(tb, 1);
            _hkBoxes[i] = tb;

            var btn = new Button
            {
                Content = "▶", Style = hkBtnStyle,
                Width = 24, Height = 22,
                Margin = new Thickness(3, 0, 0, 0),
                ToolTip = $"Play hotkey {i + 1}"
            };
            btn.Click += (_, _) =>
            {
                var name = _hkBoxes[idx].Text.Trim();
                if (!string.IsNullOrEmpty(name) && _connected)
                    _serial.Send($"goto {name}");
            };
            Grid.SetColumn(btn, 2);

            row.Children.Add(label);
            row.Children.Add(tb);
            row.Children.Add(btn);
            HotkeyPanel.Children.Add(row);
        }
    }

    private void SaveHotkeys()
    {
        var names = new string[HotkeyCount];
        for (int i = 0; i < HotkeyCount; i++) names[i] = _hkBoxes[i].Text;
        File.WriteAllText(HotkeyFile, JsonSerializer.Serialize(names));
    }

    private void LoadHotkeys()
    {
        if (!File.Exists(HotkeyFile)) return;
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(HotkeyFile));
            if (names == null) return;
            for (int i = 0; i < HotkeyCount && i < names.Length; i++)
                _hkBoxes[i].Text = names[i] ?? "";
        }
        catch { }
    }

    // ── Serial receive ────────────────────────────────────────────────────────

    private void OnLineReceived(string line)
    {
        Dispatcher.Invoke(() =>
        {
            if (line.StartsWith("STATUS "))
                ParseStatus(line);
            else if (line.StartsWith("VERSION "))
                ParseVersion(line);
            else
            {
                Log(line);
                ParsePlayingLine(line);
            }
        });
    }

    private void SlVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TbVolume == null) return;
        int v = (int)SlVolume.Value;
        TbVolume.Text = $"{v}%";
        if (SlSigVolume != null && (int)SlSigVolume.Value != v) SlSigVolume.Value = v;
        if (_connected && _volumeSync) _serial.Send($"volume {v}");
    }

    private static string EqDb(double v)
    {
        double db = (v - 100) * 0.12;
        return db >= 0 ? $"+{db:F1}" : $"{db:F1}";
    }

    private static string FreqLabel(float f) =>
        f >= 1000 ? $"{f / 1000:0.##}k" : $"{(int)f}";

    private void BuildEqSliders()
    {
        for (int i = 0; i < EqBands; i++)
        {
            int idx = i;
            var sp = new StackPanel { Width = 48, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(3, 0, 3, 0) };

            var dbLbl = new TextBlock
            {
                Text = " 0.0", FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _eqDbLabels[idx] = dbLbl;

            var sl = new Slider
            {
                Style = (Style)FindResource("AVFaderV"),
                Height = 110,
                Minimum = 0, Maximum = 200, Value = 100,
                IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Center,
                SmallChange = 1, LargeChange = 10
            };
            sl.ValueChanged += (_, _) =>
            {
                if (_eqDbLabels[idx] == null) return;
                _eqDbLabels[idx].Text = EqDb(sl.Value);
                if (_connected && _eqSync) _serial.Send($"eq band {idx} {(int)sl.Value}");
            };
            _eqSliders[idx] = sl;

            var freqLbl = new TextBlock
            {
                Text = FreqLabel(EqFreqs[idx]),
                Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8)),
                FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };

            sp.Children.Add(dbLbl);
            sp.Children.Add(sl);
            sp.Children.Add(freqLbl);
            EqPanel.Children.Add(sp);
        }
    }

    private void BtnEqReset_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show("Reset all 10 EQ bands to flat (0 dB)?",
                                "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _eqSync = false;
        foreach (var s in _eqSliders) if (s != null) s.Value = 100;
        _eqSync = true;
        _serial.Send("eq reset");
    }

    private void ParseVersion(string line)
    {
        string Get(string key)
        {
            int i = line.IndexOf(key + "=");
            if (i < 0) return "";
            int start = i + key.Length + 1;
            int end = line.IndexOf(' ', start);
            return end < 0 ? line[start..] : line[start..end];
        }
        var fw    = Get("firmware");
        var board = Get("board");
        var build = line.Contains("build=") ? line[(line.IndexOf("build=") + 6)..].TrimEnd() : "";
        TbFirmware.Text = $"{board}  fw {fw}  ({build})";
        Log($"[FW] {board} firmware {fw} built {build}");
    }

    private void ParseStatus(string line)
    {
        string Get(string key)
        {
            int i = line.IndexOf(key + "=");
            if (i < 0) return "";
            int start = i + key.Length + 1;
            if (start < line.Length && line[start] == '"')
            {
                start++;
                int end = line.IndexOf('"', start);
                return end < 0 ? line[start..] : line[start..end];
            }
            else
            {
                int end = line.IndexOf(' ', start);
                return end < 0 ? line[start..] : line[start..end];
            }
        }

        var elapsedMs = long.TryParse(Get("elapsed"), out var ms) ? ms : -1;
        var track     = Get("track");
        var total     = Get("total");
        var state     = Get("state");
        var file      = Get("file");
        var repeat    = Get("repeat");
        var autoplay  = Get("autoplay");

        if (elapsedMs >= 0)
        {
            long secs = elapsedMs / 1000;
            TbElapsed.Text = $"{secs / 60:D2}:{secs % 60:D2}";
        }

        if (!string.IsNullOrEmpty(track) && !string.IsNullOrEmpty(total))
        {
            TbTrackInfo.Text = $"Track {track} / {total}  [{state}]";
            if (int.TryParse(track, out var tn)) _currentTrack = tn;
            if (int.TryParse(total, out var tt)) _totalTracks  = tt;
        }

        if (!string.IsNullOrEmpty(file))
        {
            var stem = IOPath.GetFileNameWithoutExtension(file);
            if (TbNowPlaying.Text != $"♪  {stem}") TbNowPlaying.Text = $"♪  {stem}";
        }

        var samplehz = Get("samplehz");
        var ext = !string.IsNullOrEmpty(file) ? IOPath.GetExtension(file).TrimStart('.').ToUpper() : "";
        if (!string.IsNullOrEmpty(ext)) TbFileCodec.Text = ext;
        if (!string.IsNullOrEmpty(samplehz) && long.TryParse(samplehz, out var hz))
            TbFileSampleRate.Text = hz >= 1000 ? $"{hz / 1000.0:0.###} kHz" : $"{hz} Hz";

        var monoStr = Get("mono");
        if (!string.IsNullOrEmpty(monoStr))
        {
            bool isMono = monoStr == "on";
            if (isMono != _mono) { _mono = isMono; UpdateMonoToggleUI(isMono); }
        }

        var volStr = Get("vol");
        if (!_volumeSync && !string.IsNullOrEmpty(volStr) && int.TryParse(volStr, out var v))
        {
            _volumeSync = true;
            SlVolume.Value = v;
        }

        if (!_eqSync)
        {
            var eqStr = Get("eq");
            if (!string.IsNullOrEmpty(eqStr))
            {
                var parts = eqStr.Split(',');
                if (parts.Length == EqBands)
                {
                    _eqSync = true;
                    for (int i = 0; i < EqBands; i++)
                        if (int.TryParse(parts[i], out var ev) && _eqSliders[i] != null)
                            _eqSliders[i].Value = ev;
                }
            }
        }

        /* ── GUI-Scheduler playlist advance ───────────────────────────────── */
        if (_schedMode == SchedMode.GuiScheduler &&
            _guiLoopsRemain != 0 && _guiPlaylist.Length > 0)
        {
            if (_lastState == "playing" && state == "stopped")
            {
                /* advance to next track in playlist */
                _guiPlPos++;
                if (_guiPlPos >= _guiPlaylist.Length)
                {
                    _guiPlPos = 0;
                    if (_guiLoopsRemain > 0) _guiLoopsRemain--;
                }
                if (_guiLoopsRemain != 0)
                {
                    _guiActiveTrack = _guiPlaylist[_guiPlPos];
                    /* check stop time before starting next track */
                    bool pastStop = false;
                    if (_guiActiveEntry is { } ae && !string.IsNullOrEmpty(ae.StopTime))
                    {
                        string nowHM = DateTime.Now.ToString("HH:mm");
                        pastStop = string.Compare(nowHM, ae.StopTime, StringComparison.Ordinal) >= 0;
                    }
                    if (!pastStop)
                    {
                        _serial.Send($"goto {_guiActiveTrack}");
                        Log($"[SCHED-GUI] → {_guiActiveTrack}  (pos={_guiPlPos} loops={_guiLoopsRemain})");
                        UpdateGuiSchedStatus();
                    }
                    else
                    {
                        GuiSchedStop("stop time reached");
                    }
                }
                else
                {
                    GuiSchedStop("playlist done");
                }
            }

            /* check stop time every tick */
            if (_guiActiveEntry is { } activeEntry && !string.IsNullOrEmpty(activeEntry.StopTime))
            {
                string nowHM = DateTime.Now.ToString("HH:mm");
                if (string.Compare(nowHM, activeEntry.StopTime, StringComparison.Ordinal) >= 0 &&
                    state == "playing")
                {
                    _serial.Send("stop");
                    GuiSchedStop("stop time reached");
                }
            }
        }

        /* ── Pico-Scheduler single-track loop (backward compat) ─────────── */
        if (_schedMode == SchedMode.PicoScheduler &&
            _schedLoopsRemaining != 0 && !string.IsNullOrEmpty(_schedFile))
        {
            if (_lastState == "playing" && state == "stopped")
            {
                if (_schedLoopsRemaining > 0) _schedLoopsRemaining--;
                if (_schedLoopsRemaining != 0)
                    _serial.Send($"goto {_schedFile}");
                else
                    _schedFile = "";
            }
        }

        _lastState = state;

        bool boardPaused = state == "paused";
        if (boardPaused != _paused)
        {
            _paused = boardPaused;
            BtnPause.Content = _paused ? "▶  RESUME" : "⏸  PAUSE";
        }

        bool modeChanged = false;
        if (!string.IsNullOrEmpty(repeat) && repeat != _repeatMode)
        { _repeatMode = repeat; modeChanged = true; SaveSettings(); }
        bool newAuto = autoplay == "on";
        if (!string.IsNullOrEmpty(autoplay) && newAuto != _autoPlay)
        { _autoPlay = newAuto; modeChanged = true; }
        if (modeChanged) UpdateModeButtons();
    }

    private void ParsePlayingLine(string line)
    {
        if (!line.Contains("] Playing:")) return;
        int idx   = line.IndexOf("Playing:") + 8;
        var token = line[idx..].Trim().Split(' ')[0];
        var stem  = IOPath.GetFileNameWithoutExtension(token);
        TbNowPlaying.Text = $"♪  {stem}";
    }

    private void Log(string text)
    {
        TbLog.Text += text + "\n";
        TbLog.ScrollToEnd();
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TbLog.Text = "";

    private void UpdateMonoToggleUI(bool mono)
    {
        MonoToggleBorder.Background = new SolidColorBrush(
            mono ? Color.FromRgb(0x89, 0xb4, 0xfa) : Color.FromRgb(0x45, 0x47, 0x5a));
        TbMonoLabel.Text       = mono ? "MONO" : "STEREO";
        TbMonoLabel.Foreground = new SolidColorBrush(
            mono ? Color.FromRgb(0x1e, 0x1e, 0x2e) : Color.FromRgb(0xa6, 0xad, 0xc8));
        MonoThumb.HorizontalAlignment = mono ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        MonoThumb.Margin = mono ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
    }

    private void MonoToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_connected) return;
        _mono = !_mono;
        UpdateMonoToggleUI(_mono);
        _serial.Send(_mono ? "mono on" : "mono off");
    }

    // ── Schedule ──────────────────────────────────────────────────────────────

    private async void SchedModeChanged(object sender, RoutedEventArgs e)
    {
        if (TbSchedStatus == null) return;   /* fired during XAML init before controls exist */
        if (sender is not RadioButton rb) return;
        bool isPico = rb.Tag?.ToString() == "pico";
        _schedMode = isPico ? SchedMode.PicoScheduler : SchedMode.GuiScheduler;

        UpdateSchedModeUI();

        if (_schedMode == SchedMode.GuiScheduler && _connected)
        {
            /* suspend Pico scheduler — entries stay intact, just paused */
            Log("[SCHED] Switching to GUI Scheduler — pausing Pico scheduler…");
            bool ok = await SendAndWaitOk("sched pause", 3000);
            Log(ok ? "[SCHED] Pico scheduler paused ✓" : "[SCHED] WARN: could not pause Pico scheduler");
            TbSchedStatus.Text = "GUI Scheduler — idle";
            _guiLoopsRemain = 0; _guiActiveTrack = ""; _guiActiveEntry = null;
            _lastSchedMinute = "";
        }
        else if (_schedMode == SchedMode.PicoScheduler && _connected)
        {
            /* resume Pico scheduler */
            Log("[SCHED] Switching to Pico Scheduler — resuming…");
            bool ok = await SendAndWaitOk("sched resume", 3000);
            Log(ok ? "[SCHED] Pico scheduler resumed ✓" : "[SCHED] WARN: could not resume Pico scheduler");
            TbSchedStatus.Text = "";
            _guiLoopsRemain = 0;
        }
        else if (_schedMode == SchedMode.PicoScheduler)
        {
            TbSchedStatus.Text = "";
            _guiLoopsRemain = 0;
        }
    }

    private void UpdateSchedModeUI()
    {
        if (BtnSendToPico == null || BtnPullFromPico == null) return;
        bool isPico = _schedMode == SchedMode.PicoScheduler;
        BtnSendToPico.Visibility  = isPico ? Visibility.Visible : Visibility.Collapsed;
        BtnPullFromPico.Visibility = isPico ? Visibility.Visible : Visibility.Collapsed;
        BtnSendToPico.IsEnabled   = isPico && _connected;
        BtnPullFromPico.IsEnabled = isPico && _connected;
    }

    private void BtnAddSchedule_Click(object sender, RoutedEventArgs e)
    {
        _schedules.Add(new ScheduleEntry());
        SaveSchedules();
        RebuildSchedulePanel();
    }

    private void BtnClearSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_schedules.Count == 0) return;
        var r = MessageBox.Show("Clear all schedule entries?", "Confirm",
                                MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _schedules.Clear();
        SaveSchedules();
        RebuildSchedulePanel();
    }

    private void RebuildSchedulePanel()
    {
        SchedulePanel.Children.Clear();
        for (int i = 0; i < _schedules.Count; i++)
            SchedulePanel.Children.Add(BuildScheduleRow(i));
    }

    private UIElement BuildScheduleRow(int idx)
    {
        var entry = _schedules[idx];

        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });   // 0 chk
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // 1 time
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // 2 stop
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 3 file
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });   // 4 loops
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(162) });  // 5 days
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });   // 6 del

        var chk = new CheckBox { IsChecked = entry.Enabled, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        chk.Checked   += (_, _) => { entry.Enabled = true;  SaveSchedules(); };
        chk.Unchecked += (_, _) => { entry.Enabled = false; SaveSchedules(); };
        Grid.SetColumn(chk, 0);

        var timeBox = MakeTimePicker(entry.Time, Color.FromRgb(0xa6, 0xe3, 0xa1),
                                      v => { entry.Time = v; SaveSchedules(); });
        Grid.SetColumn(timeBox, 1);

        var stopBox = MakeTimePicker(entry.StopTime, Color.FromRgb(0xf3, 0x8b, 0xa8),
                                     v => { entry.StopTime = v; SaveSchedules(); },
                                     allowEmpty: true);
        Grid.SetColumn(stopBox, 2);

        var tbFile = new TextBox
        {
            Text = entry.Tracks,
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xb0, 0xc8, 0xff)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x42)),
            BorderThickness = new Thickness(1), FontFamily = new FontFamily("Consolas"),
            FontSize = 11, Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Tracks (no ext), comma-separated: song1,song2,song3"
        };
        tbFile.TextChanged += (_, _) => { entry.Tracks = tbFile.Text.Trim(); SaveSchedules(); };
        Grid.SetColumn(tbFile, 3);

        var tbLoops = new TextBox
        {
            Text = entry.Loops == 0 ? "∞" : entry.Loops.ToString(),
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xf9, 0xc7, 0x4f)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x42)),
            BorderThickness = new Thickness(1), FontFamily = new FontFamily("Consolas"),
            FontSize = 12, Padding = new Thickness(4, 2, 4, 2),
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Loops: 1–99 or ∞ (infinite)"
        };
        tbLoops.TextChanged += (_, _) =>
        {
            var t = tbLoops.Text.Trim();
            if (t == "∞" || t == "inf" || t == "0") { entry.Loops = 0; SaveSchedules(); }
            else if (int.TryParse(t, out int v) && v >= 1 && v <= 99) { entry.Loops = v; SaveSchedules(); }
        };
        Grid.SetColumn(tbLoops, 4);

        var dayPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        for (int d = 0; d < 7; d++)
        {
            int di = d;
            var btn = new Button
            {
                Content = DayAbbr[d], FontFamily = new FontFamily("Consolas"),
                FontSize = 9, Width = 21, Height = 22,
                Margin = new Thickness(d == 0 ? 0 : 2, 0, 0, 0),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x42)),
                Cursor = Cursors.Hand,
                ToolTip = new[] { "Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday" }[d]
            };
            ApplyDayBtnStyle(btn, entry.Days[d]);
            btn.Click += (_, _) => { entry.Days[di] = !entry.Days[di]; ApplyDayBtnStyle(btn, entry.Days[di]); SaveSchedules(); };
            dayPanel.Children.Add(btn);
        }
        Grid.SetColumn(dayPanel, 5);

        var btnDel = new Button
        {
            Content = "✕", FontSize = 11, Width = 26, Height = 26,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x2a)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0x5a, 0x5a)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x42)),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand
        };
        btnDel.Click += (_, _) => { _schedules.RemoveAt(idx); SaveSchedules(); RebuildSchedulePanel(); };
        Grid.SetColumn(btnDel, 6);

        row.Children.Add(chk);
        row.Children.Add(timeBox);
        row.Children.Add(stopBox);
        row.Children.Add(tbFile);
        row.Children.Add(tbLoops);
        row.Children.Add(dayPanel);
        row.Children.Add(btnDel);
        return row;
    }

    /* Two-box HH:MM picker — colon is a label, cannot be deleted.
     * allowEmpty: if true, both boxes blank = no time (used for Stop). */
    private static FrameworkElement MakeTimePicker(
        string initVal, Color fg, Action<string> onChange, bool allowEmpty = false)
    {
        string hh = "", mm = "";
        if (!string.IsNullOrEmpty(initVal) && initVal.Length == 5 && initVal[2] == ':')
        { hh = initVal[..2]; mm = initVal[3..]; }

        var bg    = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x14));
        var bd    = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x42));
        var fgBr  = new SolidColorBrush(fg);
        var font  = new FontFamily("Consolas");

        TextBox MakeSegment(string init, int max) => new TextBox
        {
            Text            = init,
            Width           = 22, MaxLength = 2,
            Background      = bg, Foreground = fgBr,
            BorderBrush     = bd, BorderThickness = new Thickness(1),
            FontFamily      = font, FontSize = 13,
            Padding         = new Thickness(2, 2, 2, 2),
            TextAlignment   = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var tbH = MakeSegment(hh, 23);
        var tbM = MakeSegment(mm, 59);

        void Notify()
        {
            string h = tbH.Text.Trim(), m = tbM.Text.Trim();
            if (allowEmpty && h == "" && m == "") { onChange(""); return; }
            if (int.TryParse(h, out int hv) && int.TryParse(m, out int mv))
            {
                hv = Math.Clamp(hv, 0, 23);
                mv = Math.Clamp(mv, 0, 59);
                onChange($"{hv:D2}:{mv:D2}");
            }
        }

        tbH.PreviewTextInput += (_, e) =>
        {
            e.Handled = !char.IsDigit(e.Text, 0);
        };
        tbM.PreviewTextInput += (_, e) =>
        {
            e.Handled = !char.IsDigit(e.Text, 0);
        };

        /* Auto-advance to minutes after 2 digits */
        tbH.TextChanged += (_, _) =>
        {
            if (tbH.Text.Length == 2) tbM.Focus();
            Notify();
        };
        tbM.TextChanged += (_, _) => Notify();

        /* Lost focus: pad with zeros */
        tbH.LostFocus += (_, _) =>
        {
            if (tbH.Text.Length == 1) tbH.Text = "0" + tbH.Text;
        };
        tbM.LostFocus += (_, _) =>
        {
            if (tbM.Text.Length == 1) tbM.Text = "0" + tbM.Text;
        };

        var colon = new TextBlock
        {
            Text = ":", Foreground = new SolidColorBrush(Color.FromRgb(0x58, 0x58, 0x72)),
            FontFamily = font, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0)
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(tbH);
        panel.Children.Add(colon);
        panel.Children.Add(tbM);
        return panel;
    }

    private static void ApplyDayBtnStyle(Button btn, bool active)
    {
        btn.Background = new SolidColorBrush(active ? Color.FromRgb(0x3d, 0x5a, 0x99) : Color.FromRgb(0x0a, 0x0a, 0x18));
        btn.Foreground = new SolidColorBrush(active ? Color.FromRgb(0xb0, 0xc8, 0xff) : Color.FromRgb(0x4e, 0x4e, 0x72));
    }

    private void SaveSchedules()
    {
        try
        {
            var json = JsonSerializer.Serialize(_schedules, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ScheduleFile, json);
        }
        catch { }
    }

    private async void BtnSendToPico_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) return;
        BtnSendToPico.IsEnabled = false;
        TbSchedStatus.Text = "Sending…";
        await SendScheduleToPico();
        BtnSendToPico.IsEnabled = true;
    }

    private async System.Threading.Tasks.Task SendScheduleToPico()
    {
        /* Send one command and wait up to 4 s for any OK:/ERR: response */
        async System.Threading.Tasks.Task<bool> Send(string cmd)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            Action<string> h = null!;
            h = line =>
            {
                if (line.StartsWith("OK:") || line.StartsWith("ERR:"))
                { _serial.LineReceived -= h; tcs.TrySetResult(line.StartsWith("OK:")); }
            };
            _serial.LineReceived += h;
            _serial.Send(cmd);
            using var cts = new System.Threading.CancellationTokenSource(4000);
            cts.Token.Register(() => { _serial.LineReceived -= h; tcs.TrySetResult(false); });
            return await tcs.Task;
        }

        Log("[SCHED→PICO] Clearing existing schedule…");
        if (!await Send("sched clear"))
        { TbSchedStatus.Text = "ERR: sched clear"; Log("[SCHED→PICO] ERR: no ACK for sched clear"); return; }

        int sent = 0;
        foreach (var e in _schedules)
        {
            if (string.IsNullOrEmpty(e.Tracks)) continue;
            string days = "";
            foreach (var d in e.Days) days += d ? "1" : "0";
            string stopPart = string.IsNullOrEmpty(e.StopTime) ? "" : $" stop={e.StopTime}";
            string cmd = $"sched add time={e.Time}{stopPart} tracks={e.Tracks} loops={e.Loops} days={days} enabled={(e.Enabled ? 1 : 0)}";
            bool ok = await Send(cmd);
            if (ok) sent++;
            else    Log($"[SCHED→PICO] ERR: add {e.Tracks}");
        }

        if (!await Send("sched save"))
        { TbSchedStatus.Text = "ERR: sched save"; Log("[SCHED→PICO] ERR: sched save"); return; }

        string msg = $"Sent {sent} entr{(sent == 1 ? "y" : "ies")} to Pico ✓";
        TbSchedStatus.Text = msg;
        Log($"[SCHED→PICO] {msg}");
    }

    private System.Threading.Tasks.Task<bool> SendAndWaitOk(string cmd, int timeoutMs = 4000)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string> h = null!;
        h = line =>
        {
            if (line.StartsWith("OK:") || line.StartsWith("ERR:"))
            { _serial.LineReceived -= h; tcs.TrySetResult(line.StartsWith("OK:")); }
        };
        _serial.LineReceived += h;
        _serial.Send(cmd);
        var cts = new System.Threading.CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => { _serial.LineReceived -= h; tcs.TrySetResult(false); });
        return tcs.Task;
    }

    /* ── Pull schedule from Pico ─────────────────────────────────────────── */
    private async void BtnPullFromPico_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) return;
        BtnPullFromPico.IsEnabled = false;
        BtnSendToPico.IsEnabled   = false;
        TbSchedStatus.Text        = "Pulling from Pico…";

        var entries = await PullScheduleFromPico();
        if (entries != null)
        {
            _schedules.Clear();
            _schedules.AddRange(entries);
            SaveSchedules();
            RebuildSchedulePanel();
            string msg = $"Pulled {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} from Pico ✓";
            TbSchedStatus.Text = msg;
            Log($"[PICO→SCHED] {msg}");
        }
        else
        {
            TbSchedStatus.Text = "ERR: no response from Pico";
        }

        BtnPullFromPico.IsEnabled = true;
        BtnSendToPico.IsEnabled   = true;
    }

    private async System.Threading.Tasks.Task<List<ScheduleEntry>?> PullScheduleFromPico()
    {
        /* sched list sends (new protocol):
         *   SCHED count=N
         *   SCHED_ENTRY #i en=E ti=HH:MM st=HH:MM lp=N da=DDDDDDD
         *   SCHED_TRACKS #i tracks_csv
         *   (×N pairs, then silence)
         * We wait until we have N entries with tracks filled in, or 5 s timeout. */
        int expectedCount  = -1;
        int tracksReceived = 0;
        var collected      = new List<ScheduleEntry>();
        var tcs = new System.Threading.Tasks.TaskCompletionSource<List<ScheduleEntry>?>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

        Action<string> h = null!;
        h = line =>
        {
            if (line.StartsWith("SCHED count="))
            {
                if (int.TryParse(line["SCHED count=".Length..], out int n))
                {
                    expectedCount = n;
                    if (n == 0) { _serial.LineReceived -= h; tcs.TrySetResult(collected); }
                }
            }
            else if (line.StartsWith("SCHED_ENTRY "))
            {
                var entry = ParseSchedEntry(line);
                if (entry != null) collected.Add(entry);
            }
            else if (line.StartsWith("SCHED_TRACKS "))
            {
                /* SCHED_TRACKS #i csv */
                var rest = line["SCHED_TRACKS ".Length..].TrimStart('#');
                var sp   = rest.IndexOf(' ');
                if (sp > 0 && int.TryParse(rest[..sp], out int idx) && idx < collected.Count)
                {
                    collected[idx].Tracks = rest[(sp + 1)..];
                    tracksReceived++;
                    if (expectedCount >= 0 && tracksReceived >= expectedCount)
                    { _serial.LineReceived -= h; tcs.TrySetResult(collected); }
                }
            }
        };

        _serial.LineReceived += h;
        _serial.Send("sched list");

        using var cts = new System.Threading.CancellationTokenSource(5000);
        cts.Token.Register(() => { _serial.LineReceived -= h; tcs.TrySetResult(expectedCount < 0 ? null : collected); });

        return await tcs.Task;
    }

    private static ScheduleEntry? ParseSchedEntry(string line)
    {
        /* New:  SCHED_ENTRY #i en=E ti=HH:MM st=HH:MM lp=N da=DDDDDDD
         * Old:  SCHED_ENTRY #i enabled=E time=HH:MM stop=SS:MM loops=L days=DDDDDDD file=F */
        var e = new ScheduleEntry();
        foreach (var token in line.Split(' '))
        {
            var kv = token.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "en":
                case "enabled":  e.Enabled  = kv[1] == "1"; break;
                case "ti":
                case "time":     e.Time     = kv[1]; break;
                case "st":
                case "stop":     e.StopTime = kv[1] == "--:--" ? "" : kv[1]; break;
                case "lp":
                case "loops":    e.Loops    = int.TryParse(kv[1], out int l) ? l : 1; break;
                case "da":
                case "days":
                    for (int d = 0; d < 7 && d < kv[1].Length; d++)
                        e.Days[d] = kv[1][d] == '1';
                    break;
                case "tracks":
                case "file":     e.Tracks   = kv[1]; break;
            }
        }
        return e;
    }

    /* ── Save / Load schedule file ───────────────────────────────────────── */
    private void BtnSaveSchedFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Save Schedule",
            Filter     = "JSON Schedule (*.json)|*.json|All files (*.*)|*.*",
            FileName   = "schedule.json",
            DefaultExt = ".json"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                _schedules, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            TbSchedStatus.Text = $"Saved → {System.IO.Path.GetFileName(dlg.FileName)} ✓";
        }
        catch (Exception ex) { TbSchedStatus.Text = $"ERR: {ex.Message}"; }
    }

    private void BtnLoadSchedFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Load Schedule",
            Filter = "JSON Schedule (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<ScheduleEntry>>(
                File.ReadAllText(dlg.FileName));
            if (list == null) return;
            _schedules.Clear();
            _schedules.AddRange(list);
            SaveSchedules();
            RebuildSchedulePanel();
            TbSchedStatus.Text = $"Loaded {list.Count} entries from {System.IO.Path.GetFileName(dlg.FileName)} ✓";
        }
        catch (Exception ex) { TbSchedStatus.Text = $"ERR: {ex.Message}"; }
    }

    private void LoadSchedules()
    {
        try
        {
            if (!File.Exists(ScheduleFile)) return;
            var list = JsonSerializer.Deserialize<List<ScheduleEntry>>(File.ReadAllText(ScheduleFile));
            if (list == null) return;
            _schedules.AddRange(list);
            RebuildSchedulePanel();
        }
        catch { }
    }

    private void CheckSchedule()
    {
        if (!_connected || _schedMode != SchedMode.GuiScheduler) return;

        string nowHHMM = DateTime.Now.ToString("HH:mm");
        int    dayIdx  = ((int)DateTime.Now.DayOfWeek + 6) % 7;
        string key     = nowHHMM + "/" + dayIdx;

        if (_lastSchedMinute != "" && !_lastSchedMinute.StartsWith(nowHHMM + "/"))
            _lastSchedMinute = "";
        if (key == _lastSchedMinute) return;

        foreach (var entry in _schedules)
        {
            if (!entry.Enabled || string.IsNullOrEmpty(entry.Tracks)) continue;
            if (entry.Time != nowHHMM || !entry.Days[dayIdx]) continue;

            /* build playlist */
            _guiPlaylist    = entry.Tracks.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            _guiPlPos       = 0;
            _guiLoopsRemain = entry.Loops == 0 ? -1 : entry.Loops;
            _guiActiveTrack = _guiPlaylist[0];
            _guiActiveEntry = entry;
            _lastSchedMinute = key;

            _serial.Send($"goto {_guiActiveTrack}");
            string loopStr = entry.Loops == 0 ? "∞" : $"{entry.Loops}×";
            Log($"[SCHED-GUI] {nowHHMM} → {_guiPlaylist.Length} tracks  ({loopStr})");
            UpdateGuiSchedStatus();
            break;
        }
    }

    private void GuiSchedStop(string reason)
    {
        Log($"[SCHED-GUI] stopped: {reason}");
        _guiLoopsRemain = 0;
        _guiActiveTrack = "";
        _guiActiveEntry = null;
        UpdateGuiSchedStatus();
    }

    private void UpdateGuiSchedStatus()
    {
        if (_guiLoopsRemain == 0 || _guiPlaylist.Length == 0)
        { TbSchedStatus.Text = "GUI Scheduler — idle"; return; }
        string loops = _guiLoopsRemain < 0 ? "∞" : $"{_guiLoopsRemain} loops left";
        TbSchedStatus.Text = $"▶ {_guiActiveTrack}  [{_guiPlPos + 1}/{_guiPlaylist.Length}]  {loops}";
    }

    // ── File upload ───────────────────────────────────────────────────────────

    private async void BtnSendFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected || _uploading) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio files (*.mp3;*.flac;*.wav)|*.mp3;*.flac;*.wav",
            Title  = "Select audio file to upload to Pico SD card"
        };
        if (dlg.ShowDialog() != true) return;

        var filePath = dlg.FileName;
        var fileName = IOPath.GetFileName(filePath);
        byte[] data;
        try { data = File.ReadAllBytes(filePath); }
        catch (Exception ex) { Log($"[UPLOAD] ERR: {ex.Message}"); return; }

        _beatTimer.Stop();
        _uploading = true;
        BtnSendFile.IsEnabled      = false;
        UploadPanel.Visibility     = Visibility.Visible;
        UploadBar.Value            = 0;
        UploadPct.Text             = "0%";
        UploadLabel.Text           = $"📤 {fileName}";
        Log($"[UPLOAD] Sending {fileName} ({data.Length:N0} bytes)…");

        System.Threading.Tasks.TaskCompletionSource<bool>? pendingAck = null;
        var readyTcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

        Action<string> handler = line =>
        {
            if      (line == "READY")              readyTcs.TrySetResult(true);
            else if (line == "ACK")                pendingAck?.TrySetResult(true);
            else if (line.StartsWith("DONE"))      pendingAck?.TrySetResult(true);
            else if (line.StartsWith("ERR:"))      { readyTcs.TrySetResult(false); pendingAck?.TrySetResult(false); }
        };
        _serial.LineReceived += handler;

        bool success = false;
        try
        {
            _serial.Send($"upload {fileName} {data.Length}");

            if (await System.Threading.Tasks.Task.WhenAny(readyTcs.Task, System.Threading.Tasks.Task.Delay(10000)) != readyTcs.Task
                || !readyTcs.Task.Result)
            {
                Log("[UPLOAD] ERR: no READY from firmware"); return;
            }

            int sent = 0;
            while (sent < data.Length)
            {
                int count = Math.Min(UploadChunk, data.Length - sent);
                pendingAck = new System.Threading.Tasks.TaskCompletionSource<bool>(
                    System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                _serial.SendBytes(data, sent, count);
                sent += count;

                if (await System.Threading.Tasks.Task.WhenAny(pendingAck.Task, System.Threading.Tasks.Task.Delay(10000)) != pendingAck.Task
                    || !pendingAck.Task.Result)
                {
                    Log("[UPLOAD] ERR: ACK timeout"); return;
                }

                double pct = (double)sent / data.Length * 100;
                UploadBar.Value = pct;
                UploadPct.Text  = $"{pct:F0}%";
            }
            Log($"[UPLOAD] Done ✓  {fileName}");
            success = true;
        }
        finally
        {
            _serial.LineReceived -= handler;
            _uploading = false;
            if (_connected) _beatTimer.Start();
            BtnSendFile.IsEnabled  = _connected;
            UploadPanel.Visibility = Visibility.Collapsed;
            if (!success) Log("[UPLOAD] Transfer incomplete");
        }
    }

    // ── MIDI CUE ──────────────────────────────────────────────────────────────

    private void RefreshMidiPorts()
    {
        CbMidiPort.Items.Clear();
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            CbMidiPort.Items.Add(MidiIn.DeviceInfo(i).ProductName);
        if (CbMidiPort.Items.Count > 0) CbMidiPort.SelectedIndex = 0;
    }

    private void BtnMidiRefresh_Click(object sender, RoutedEventArgs e) => RefreshMidiPorts();

    private void TgMidiEnable_Click(object sender, RoutedEventArgs e)
    {
        if (TgMidiEnable.IsChecked == true) OpenMidi();
        else CloseMidi();
    }

    private void OpenMidi()
    {
        if (CbMidiPort.SelectedIndex < 0)
        {
            TbMidiStatus.Text = "ERR: ไม่พบ MIDI port";
            TgMidiEnable.IsChecked = false;
            return;
        }
        try
        {
            _midiIn = new MidiIn(CbMidiPort.SelectedIndex);
            _midiIn.MessageReceived += OnMidiMessage;
            _midiIn.ErrorReceived   += (_, e) => Dispatcher.Invoke(() => MidiLog($"[ERR] MIDI error"));
            _midiIn.Start();
            TbMidiStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
            TbMidiStatus.Text = $"● {CbMidiPort.SelectedItem}";
        }
        catch (Exception ex)
        {
            TbMidiStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xf3, 0x8b, 0xa8));
            TbMidiStatus.Text = $"ERR: {ex.Message}";
            TgMidiEnable.IsChecked = false;
        }
    }

    private void CloseMidi()
    {
        _midiIn?.Stop();
        _midiIn?.Dispose();
        _midiIn = null;
        if (TbMidiStatus != null)
        {
            TbMidiStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a));
            TbMidiStatus.Text = "—";
        }
    }

    private void OnMidiMessage(object? sender, MidiInMessageEventArgs e)
    {
        if (e.MidiEvent is NoteOnEvent noteOn && noteOn.Velocity > 0)
        {
            int note = noteOn.NoteNumber;
            var cue  = _midiCues.FirstOrDefault(c => c.Note == note);
            Dispatcher.Invoke(() =>
            {
                if (cue != null)
                {
                    ExecuteMidiCue(cue);
                    MidiLog($"Note {note,3} ({NoteName(note),-3})  vel={noteOn.Velocity,-3}  →  {cue.Command}{(cue.Command == "goto" ? " " + cue.Track : "")}");
                }
                else
                {
                    MidiLog($"Note {note,3} ({NoteName(note),-3})  vel={noteOn.Velocity,-3}  (no CUE)");
                }
            });
        }
    }

    private void ExecuteMidiCue(MidiCueEntry cue)
    {
        if (!_connected) return;
        switch (cue.Command)
        {
            case "goto": if (!string.IsNullOrEmpty(cue.Track)) _serial.Send($"goto {cue.Track}"); break;
            case "play": _serial.Send("play"); break;
            case "stop": _serial.Send("stop"); break;
            case "next": _serial.Send("next"); break;
            case "prev": _serial.Send("prev"); break;
        }
    }

    private void MidiLog(string text)
    {
        TbMidiLog.Text += text + "\n";
        TbMidiLog.ScrollToEnd();
    }

    private void BtnMidiLogClear_Click(object sender, RoutedEventArgs e) => TbMidiLog.Text = "";

    private void BtnAddMidiCue_Click(object sender, RoutedEventArgs e)
    {
        _midiCues.Add(new MidiCueEntry { Note = 60 + _midiCues.Count });
        SaveMidiCues();
        RebuildMidiCuePanel();
    }

    private void RebuildMidiCuePanel()
    {
        MidiCuePanel.Children.Clear();
        for (int i = 0; i < _midiCues.Count; i++)
            MidiCuePanel.Children.Add(BuildMidiCueRow(i));
    }

    private UIElement BuildMidiCueRow(int idx)
    {
        var entry = _midiCues[idx];
        var row   = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        // Note # textbox
        var tbNote = new TextBox
        {
            Text = entry.Note.ToString(),
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xf9, 0xc7, 0x4f)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x42)),
            BorderThickness = new Thickness(1), FontFamily = new FontFamily("Consolas"),
            FontSize = 13, Padding = new Thickness(4, 2, 4, 2),
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "MIDI note number 0–127  (C3=60)"
        };

        // Note name label (auto-updated)
        var tbName = new TextBlock
        {
            Text = NoteName(entry.Note),
            Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa)),
            FontFamily = new FontFamily("Consolas"), FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
        };

        tbNote.TextChanged += (_, _) =>
        {
            if (int.TryParse(tbNote.Text, out int n) && n >= 0 && n <= 127)
            {
                entry.Note = n;
                tbName.Text = NoteName(n);
                SaveMidiCues();
            }
        };
        Grid.SetColumn(tbNote, 0);
        Grid.SetColumn(tbName, 1);

        // Command combobox
        var cbCmd = new ComboBox
        {
            Height = 26, Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas"), FontSize = 13,
            Style = (Style)FindResource("DarkComboBox")
        };
        foreach (var cmd in new[] { "goto", "play", "stop", "next", "prev" })
            cbCmd.Items.Add(cmd);
        cbCmd.SelectedItem = entry.Command;
        cbCmd.SelectionChanged += (_, _) =>
        {
            if (cbCmd.SelectedItem is string s) { entry.Command = s; SaveMidiCues(); }
        };
        Grid.SetColumn(cbCmd, 2);

        // Track textbox
        var tbTrack = new TextBox
        {
            Text = entry.Track,
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xb0, 0xc8, 0xff)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x42)),
            BorderThickness = new Thickness(1), FontFamily = new FontFamily("Consolas"),
            FontSize = 13, Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "ชื่อไฟล์ไม่ต้องใส่นามสกุล — ใช้เมื่อ Command = goto"
        };
        tbTrack.TextChanged += (_, _) => { entry.Track = tbTrack.Text.Trim(); SaveMidiCues(); };
        Grid.SetColumn(tbTrack, 3);

        // Delete button
        var btnDel = new Button
        {
            Content = "✕", FontSize = 11, Width = 26, Height = 26,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x2a)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0x5a, 0x5a)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x42)),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        btnDel.Click += (_, _) => { _midiCues.RemoveAt(idx); SaveMidiCues(); RebuildMidiCuePanel(); };
        Grid.SetColumn(btnDel, 4);

        row.Children.Add(tbNote);
        row.Children.Add(tbName);
        row.Children.Add(cbCmd);
        row.Children.Add(tbTrack);
        row.Children.Add(btnDel);
        return row;
    }

    private void SaveMidiCues()
    {
        try { File.WriteAllText(MidiCueFile, JsonSerializer.Serialize(_midiCues, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    private void LoadMidiCues()
    {
        try
        {
            if (!File.Exists(MidiCueFile)) return;
            var list = JsonSerializer.Deserialize<List<MidiCueEntry>>(File.ReadAllText(MidiCueFile));
            if (list == null) return;
            _midiCues.AddRange(list);
            RebuildMidiCuePanel();
        }
        catch { }
    }

    // ── Window state ──────────────────────────────────────────────────────────

    private void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(WindowStateFile)) return;
            var doc = JsonDocument.Parse(File.ReadAllText(WindowStateFile)).RootElement;
            Width       = doc.GetProperty("w").GetDouble();
            Height      = doc.GetProperty("h").GetDouble();
            Left        = doc.GetProperty("x").GetDouble();
            Top         = doc.GetProperty("y").GetDouble();
            WindowState = doc.GetProperty("maximized").GetBoolean() ? WindowState.Maximized : WindowState.Normal;
        }
        catch { }
    }

    private void SaveWindowState()
    {
        try
        {
            var s = WindowState;
            var w = s == WindowState.Maximized ? RestoreBounds.Width  : Width;
            var h = s == WindowState.Maximized ? RestoreBounds.Height : Height;
            var x = s == WindowState.Maximized ? RestoreBounds.Left   : Left;
            var y = s == WindowState.Maximized ? RestoreBounds.Top    : Top;
            File.WriteAllText(WindowStateFile,
                $"{{\"w\":{w},\"h\":{h},\"x\":{x},\"y\":{y},\"maximized\":{(s == WindowState.Maximized ? "true" : "false")}}}");
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowState();
        _clockTimer.Stop();
        _beatTimer.Stop();
        _reconnectTimer.Stop();
        CloseMidi();
        _serial.Dispose();
        base.OnClosed(e);
    }
}
