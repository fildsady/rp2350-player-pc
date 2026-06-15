using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace RP2350Player;

public partial class MainWindow : Window
{
    private readonly SerialService   _serial      = new();
    private readonly DispatcherTimer _clockTimer  = new();
    private readonly DispatcherTimer _beatTimer   = new();
    private bool   _connected   = false;
    private bool   _paused      = false;
    private string _repeatMode  = "";
    private bool   _autoPlay    = true;
    private bool   _volumeSync  = false;
    private bool   _eqSync      = false;
    private bool   _mono        = false;
    private bool   _uploading   = false;
    private const int UploadChunk = 512;

    private const int EqBands = 32;
    private static readonly float[] EqFreqs = {
        16, 20, 25, 31.5f, 40, 50, 63, 80,
        100, 125, 160, 200, 250, 315, 400, 500,
        630, 800, 1000, 1250, 1600, 2000, 2500, 3150,
        4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000
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
        BuildHotkeySlots();
        LoadHotkeys();
        BuildEqSliders();
        LoadSchedules();
        RefreshPorts();

        _serial.LineReceived += OnLineReceived;
        _serial.Disconnected += () => Dispatcher.Invoke(() =>
        {
            Log("[ERR] Board disconnected");
            SetConnected(false);
        });

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

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var ports = SerialService.GetPortNames();
        CbPort.Items.Clear();
        foreach (var p in ports) CbPort.Items.Add(p);
        if (CbPort.Items.Count == 0) return;
        string last = File.Exists(LastPortFile) ? File.ReadAllText(LastPortFile).Trim() : "";
        int idx = CbPort.Items.IndexOf(last);
        CbPort.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_connected)
        {
            _beatTimer.Stop();
            _serial.Close();
            SetConnected(false);
            return;
        }
        if (CbPort.SelectedItem is not string port) return;
        if (_serial.Open(port))
        {
            File.WriteAllText(LastPortFile, port);
            SetConnected(true);
            _beatTimer.Start();
            _serial.Send("version");
        }
        else
            Log($"[ERR] Cannot open {port}");
    }

    private void SetConnected(bool on)
    {
        _connected = on;
        if (!on) { _beatTimer.Stop(); TbFirmware.Text = ""; _volumeSync = false; _eqSync = false; _mono = false; UpdateMonoToggleUI(false); }
        BtnConnect.Content        = on ? "Disconnect" : "Connect";
        StatusDot.Fill            = on ? Brushes.LightGreen : new SolidColorBrush(Color.FromRgb(0xf3,0x8b,0xa8));
        BtnPrev.IsEnabled         = on;
        BtnPlay.IsEnabled         = on;
        BtnPause.IsEnabled        = on;
        BtnStop.IsEnabled         = on;
        BtnNext.IsEnabled         = on;
        BtnGoto.IsEnabled         = on;
        BtnSyncTime.IsEnabled     = on;
        SlVolume.IsEnabled        = on;
        BtnRepeatOne.IsEnabled    = on;
        BtnRepeatAll.IsEnabled    = on;
        BtnRepeatOff.IsEnabled    = on;
        BtnRepeatSingle.IsEnabled = on;
        BtnAutoPlay.IsEnabled     = on;
        BtnEqReset.IsEnabled      = on;
        foreach (var s in _eqSliders) if (s != null) s.IsEnabled = on;
        MonoToggleBorder.IsEnabled = on;
        BtnSendFile.IsEnabled      = on && !_uploading;
        UpdateSchedModeUI();
        if (!on)
        {
            TbNowPlaying.Text = "— Not connected —"; TbElapsed.Text = "--:--"; TbTrackInfo.Text = "";
            _guiLoopsRemain = 0; _guiActiveTrack = ""; _guiActiveEntry = null;
        }
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    private void BtnPrev_Click (object sender, RoutedEventArgs e) => _serial.Send("prev");
    private void BtnNext_Click (object sender, RoutedEventArgs e) => _serial.Send("next");
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

    private void BtnRepeatOne_Click   (object sender, RoutedEventArgs e) => _serial.Send("mode repeat_one");
    private void BtnRepeatAll_Click   (object sender, RoutedEventArgs e) => _serial.Send("mode repeat_all");
    private void BtnRepeatOff_Click   (object sender, RoutedEventArgs e) => _serial.Send("mode repeat_off");
    private void BtnRepeatSingle_Click(object sender, RoutedEventArgs e) => _serial.Send("mode repeat_single");
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
        var dark = new SolidColorBrush(Color.FromRgb(0x0d, 0x14, 0x30));
        BtnRepeatOne.Foreground    = _repeatMode == "one"    ? dark : Brushes.White;
        BtnRepeatAll.Foreground    = _repeatMode == "all"    ? dark : Brushes.White;
        BtnRepeatOff.Foreground    = _repeatMode == "off"    ? dark : Brushes.White;
        BtnRepeatSingle.Foreground = _repeatMode == "single" ? dark : Brushes.White;

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
            var sp = new StackPanel { Width = 26, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(1, 0, 1, 0) };

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
        var r = MessageBox.Show("Reset all 32 EQ bands to flat (0 dB)?",
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
            TbTrackInfo.Text = $"Track {track} / {total}  [{state}]";

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
        { _repeatMode = repeat; modeChanged = true; }
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
        if (sender is not RadioButton rb) return;
        bool isPico = rb.Tag?.ToString() == "pico";
        _schedMode = isPico ? SchedMode.PicoScheduler : SchedMode.GuiScheduler;

        UpdateSchedModeUI();

        if (_schedMode == SchedMode.GuiScheduler && _connected)
        {
            /* clear Pico schedule so it doesn't conflict */
            Log("[SCHED] Switching to GUI Scheduler — clearing Pico schedule…");
            bool ok = await SendAndWaitOk("sched clear", 3000);
            if (ok) await SendAndWaitOk("sched save", 3000);
            Log(ok ? "[SCHED] Pico schedule cleared ✓" : "[SCHED] WARN: could not clear Pico schedule");
            TbSchedStatus.Text = "GUI Scheduler — idle";
            /* reset GUI state */
            _guiLoopsRemain = 0; _guiActiveTrack = ""; _guiActiveEntry = null;
            _lastSchedMinute = "";
        }
        else if (_schedMode == SchedMode.PicoScheduler)
        {
            TbSchedStatus.Text = "";
            _guiLoopsRemain = 0;
        }
    }

    private void UpdateSchedModeUI()
    {
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
        /* sched list sends:
         *   SCHED count=N
         *   SCHED_ENTRY #i enabled=E time=HH:MM stop=SS:MM loops=L days=DDDDDDD file=F
         *   (×N, then silence)
         * Collect until we have count lines or 3 s timeout. */
        int expectedCount = -1;
        var collected     = new List<ScheduleEntry>();
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
                if (expectedCount >= 0 && collected.Count >= expectedCount)
                { _serial.LineReceived -= h; tcs.TrySetResult(collected); }
            }
        };

        _serial.LineReceived += h;
        _serial.Send("sched list");

        using var cts = new System.Threading.CancellationTokenSource(3000);
        cts.Token.Register(() => { _serial.LineReceived -= h; tcs.TrySetResult(expectedCount < 0 ? null : collected); });

        return await tcs.Task;
    }

    private static ScheduleEntry? ParseSchedEntry(string line)
    {
        /* SCHED_ENTRY #i enabled=E time=HH:MM stop=SS:MM loops=L days=DDDDDDD file=F */
        var e = new ScheduleEntry();
        foreach (var token in line.Split(' '))
        {
            var kv = token.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "enabled": e.Enabled  = kv[1] == "1"; break;
                case "time":    e.Time     = kv[1]; break;
                case "stop":    e.StopTime = kv[1] == "--:--" ? "" : kv[1]; break;
                case "loops":   e.Loops    = int.TryParse(kv[1], out int l) ? l : 1; break;
                case "days":
                    for (int d = 0; d < 7 && d < kv[1].Length; d++)
                        e.Days[d] = kv[1][d] == '1';
                    break;
                case "tracks":  e.Tracks   = kv[1]; break;
                case "file":    e.Tracks   = kv[1]; break;  // backward compat
            }
        }
        return string.IsNullOrEmpty(e.Tracks) ? null : e;
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
        _serial.Dispose();
        base.OnClosed(e);
    }
}
