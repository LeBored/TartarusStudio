using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TartarusStudio.Core;
using CoreMouse = TartarusStudio.Core.MouseAction;
using CorePhysicalKey = TartarusStudio.Core.PhysicalKey;

namespace TartarusStudio.App;

public partial class MainWindow : Window
{
    private static readonly string[] ActionNames = { "Default key", "Keyboard key / combo", "Mouse function", "Macro", "Type text", "Launch program", "Switch keymap", "Switch profile", "Disabled" };
    private static readonly string[] PlaybackNames = { "Play once", "Repeat while held", "Toggle on / off" };
    private static readonly string[] SwitchNames = { "Next keymap", "Previous keymap", "Go to keymap", "Hold for keymap (shift)" };

    private readonly Controller _c;
    private readonly Dictionary<string, (Button Button, TextBlock Label)> _keys = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _loading;
    private Profile _profile;
    private int _keymapIndex;
    private string? _selectedKey;
    private Macro? _macro;
    private readonly Stopwatch _recordClock = new();
    private readonly HashSet<string> _recordDown = new();
    private int _calibrating = -1;

    // The XAML loader needs a parameterless constructor at design time.
    public MainWindow() : this(new Controller()) { }

    internal MainWindow(Controller controller)
    {
        _c = controller;
        _profile = _c.Engine.Profile;
        InitializeComponent();
        BuildKeypad();

        ActionTypeBox.ItemsSource = ActionNames;
        KeyBox.ItemsSource = KeyTable.All;
        MouseBox.ItemsSource = Enum.GetValues<CoreMouse>();
        StepMouseBox.ItemsSource = new[] { CoreMouse.Left, CoreMouse.Right, CoreMouse.Middle, CoreMouse.Back, CoreMouse.Forward };
        StepMouseBox.SelectedIndex = 0;
        PlaybackBox.ItemsSource = PlaybackNames;
        SwitchModeBox.ItemsSource = SwitchNames;

        Wire();
        RefreshProfiles();
        RefreshMacros();
        LoadSettings();

        _c.Log += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _c.StateChanged += () => Dispatcher.UIThread.Post(RefreshHeader);
        _c.DeviceKeyObserved += usage => Dispatcher.UIThread.Post(() => OnCalibrationKey(usage));
        _timer.Tick += (_, _) => { _c.ForegroundTick(); StatusText.Text = _c.BackendStatus; };
        _timer.Start();
        RefreshHeader();
        AppendLog($"Settings file: {_c.ConfigPath}");

        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnTunnelKeyUp, RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            _c.Save();
            if (_c.Config.Settings.CloseToTray && Controller.IsWindows) { e.Cancel = true; Hide(); }
            else (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        };
    }

    private Keymap Keymap => _profile.Keymaps[Math.Clamp(_keymapIndex, 0, _profile.Keymaps.Count - 1)];

    private void AppendLog(string line)
    {
        var text = (LogBox.Text ?? "") + $"{DateTime.Now:HH:mm:ss}  {line}\n";
        if (text.Length > 20000) text = text[^15000..];
        LogBox.Text = text;
        LogBox.CaretIndex = text.Length;
    }

    private static void Fill(SelectingItemsControl box, System.Collections.IEnumerable items, object? selected)
    {
        box.ItemsSource = null;
        box.ItemsSource = items;
        box.SelectedItem = selected;
    }

    // ---------------------------------------------------------------------------------------
    // Header
    // ---------------------------------------------------------------------------------------
    private void RefreshHeader()
    {
        var active = _c.Engine.Profile;
        var km = _c.Engine.ActiveKeymap;
        ActiveText.Text = $"Active: {active.Name}  ·  {km.Name}";
        LedDotR.Fill = km.LedRed ? Brushes.Red : new SolidColorBrush(Color.Parse("#3A4146"));
        LedDotG.Fill = km.LedGreen ? Brushes.LimeGreen : new SolidColorBrush(Color.Parse("#3A4146"));
        LedDotB.Fill = km.LedBlue ? Brushes.DodgerBlue : new SolidColorBrush(Color.Parse("#3A4146"));
        // Follow profile switches made by the device or by auto-switching while the window is in the background.
        if (!IsActive && !ReferenceEquals(active, _profile)) { _profile = active; _keymapIndex = 0; RefreshProfiles(); }
    }

    // ---------------------------------------------------------------------------------------
    // Profiles
    // ---------------------------------------------------------------------------------------
    private void RefreshProfiles()
    {
        _loading = true;
        if (!_c.Config.Profiles.Contains(_profile)) _profile = _c.Config.DefaultProfile;
        Fill(ProfileList, _c.Config.Profiles.ToList(), _profile);
        ProfileNameBox.Text = _profile.Name;
        DefaultProfileBox.IsChecked = _c.Config.DefaultProfileId == _profile.Id;
        Fill(ProgramList, _profile.LinkedPrograms.ToList(), null);
        LightingTitle.Text = $"Lighting for \"{_profile.Name}\"";
        BrightnessSlider.Value = _profile.Lighting.Brightness;
        BrightnessText.Text = _profile.Lighting.Brightness.ToString();
        PulsateBox.IsChecked = _profile.Lighting.Pulsate;
        _loading = false;
        RefreshKeymaps();
    }

    private void SelectProfile(Profile p)
    {
        _profile = p;
        _keymapIndex = 0;
        _c.ActivateProfile(p);
        RefreshProfiles();
        RefreshHeader();
    }

    private string UniqueName(string wanted)
    {
        string name = wanted;
        for (int i = 2; _c.Config.Profiles.Any(p => p.Name == name); i++) name = $"{wanted} {i}";
        return name;
    }

    private async void ImportProfile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import profile", FileTypeFilter = new[] { new FilePickerFileType("Profile") { Patterns = new[] { "*.json" } } } });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            var p = ConfigStore.ImportProfile(File.ReadAllText(path));
            p.Name = UniqueName(p.Name);
            _c.Engine.Sync(() => _c.Config.Profiles.Add(p));
            SelectProfile(p);
            _c.Save();
        }
        catch (Exception ex) { AppendLog("Import failed: " + ex.Message); Tabs.SelectedIndex = 3; }
    }

    private async void ExportProfile()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export profile", SuggestedFileName = _profile.Name + ".json", DefaultExtension = "json" });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try { File.WriteAllText(path, ConfigStore.ExportProfile(_profile)); }
        catch (Exception ex) { AppendLog("Export failed: " + ex.Message); Tabs.SelectedIndex = 3; }
    }

    private async void AddProgram()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose the game or program", AllowMultiple = true, FileTypeFilter = new[] { new FilePickerFileType("Programs") { Patterns = new[] { "*.exe" } } } });
        foreach (var f in files)
        {
            var name = f.TryGetLocalPath() is { } p ? Path.GetFileName(p) : f.Name;
            if (!_profile.LinkedPrograms.Contains(name, StringComparer.OrdinalIgnoreCase)) _c.Engine.Sync(() => _profile.LinkedPrograms.Add(name));
        }
        RefreshProfiles();
        _c.Save();
    }

    // ---------------------------------------------------------------------------------------
    // Keymaps and keypad
    // ---------------------------------------------------------------------------------------
    private void BuildKeypad()
    {
        foreach (var k in DeviceLayout.Keys)
        {
            var label = new TextBlock { FontSize = k.W < 60 ? 10 : 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxLines = 2 };
            var grid = new Grid();
            grid.Children.Add(new TextBlock { Text = k.Label, FontSize = 9, Foreground = new SolidColorBrush(Color.Parse("#8E979C")), Margin = new Thickness(3, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
            grid.Children.Add(label);
            var button = new Button { Width = k.W, Height = k.H, Content = grid };
            button.Classes.Add("key");
            Canvas.SetLeft(button, k.X);
            Canvas.SetTop(button, k.Y);
            string id = k.Id;
            button.Click += (_, _) => SelectKey(id);
            KeyCanvas.Children.Add(button);
            _keys[k.Id] = (button, label);
        }
    }

    private void RefreshKeymaps()
    {
        _loading = true;
        _keymapIndex = Math.Clamp(_keymapIndex, 0, _profile.Keymaps.Count - 1);
        KeymapBox.ItemsSource = _profile.Keymaps.Select((k, i) => $"{i + 1}. {k.Name}").ToList();
        KeymapBox.SelectedIndex = _keymapIndex;
        KeymapNameBox.Text = Keymap.Name;
        LedRBox.IsChecked = Keymap.LedRed; LedGBox.IsChecked = Keymap.LedGreen; LedBBox.IsChecked = Keymap.LedBlue;
        AddKeymapBtn.IsEnabled = _profile.Keymaps.Count < Profile.MaxKeymaps;
        RemoveKeymapBtn.IsEnabled = _profile.Keymaps.Count > 1;
        _loading = false;
        RefreshKeys();
        SelectKey(_selectedKey);
    }

    private void RefreshKeys()
    {
        foreach (var (id, (button, label)) in _keys)
        {
            var action = Keymap.Bindings.GetValueOrDefault(id);
            label.Text = ActionText.Describe(action, _c.Config, id);
            button.Classes.Set("bound", action != null && action.Type != ActionType.Default);
            button.Classes.Set("selected", id == _selectedKey);
        }
    }

    private static string KeyTitle(CorePhysicalKey k) => k.Id switch
    {
        "ThumbButton" => "Thumb button (16)", "ThumbKey" => "Thumb key (spacebar lever)",
        "DUp" => "Thumb-pad up", "DDown" => "Thumb-pad down", "DLeft" => "Thumb-pad left", "DRight" => "Thumb-pad right",
        _ => "Key " + k.Label,
    };

    private void SelectKey(string? id)
    {
        _selectedKey = id;
        RefreshKeys();
        var key = id == null ? null : DeviceLayout.Get(id);
        EditorPanel.IsVisible = key != null;
        SelKeyHint.IsVisible = key == null;
        if (key == null) { SelKeyTitle.Text = "Select a key"; return; }
        SelKeyTitle.Text = KeyTitle(key);
        LoadEditor(Keymap.Bindings.GetValueOrDefault(key.Id) ?? new KeyAction());
    }

    private void LoadEditor(KeyAction a)
    {
        _loading = true;
        ActionTypeBox.SelectedIndex = (int)a.Type;
        KeyBox.SelectedItem = KeyTable.Get(a.Key);
        ModCtrl.IsChecked = a.Modifiers.Any(m => m is "LCtrl" or "RCtrl");
        ModShift.IsChecked = a.Modifiers.Any(m => m is "LShift" or "RShift");
        ModAlt.IsChecked = a.Modifiers.Any(m => m is "LAlt" or "RAlt");
        ModWin.IsChecked = a.Modifiers.Any(m => m is "LWin" or "RWin");
        CaptureBox.Text = "";
        MouseBox.SelectedItem = a.Mouse;
        Fill(MacroBox, _c.Config.Macros.ToList(), _c.Config.Macros.FirstOrDefault(m => m.Id == a.MacroId));
        PlaybackBox.SelectedIndex = (int)a.Playback;
        TextValueBox.Text = a.Text ?? "";
        PathBox.Text = a.Path ?? "";
        ArgsBox.Text = a.Args ?? "";
        SwitchModeBox.SelectedIndex = (int)a.SwitchMode;
        KeymapTargetBox.ItemsSource = _profile.Keymaps.Select((k, i) => $"{i + 1}. {k.Name}").ToList();
        KeymapTargetBox.SelectedIndex = Math.Clamp(a.KeymapIndex, 0, _profile.Keymaps.Count - 1);
        Fill(ProfileTargetBox, _c.Config.Profiles.ToList(), _c.Config.Profiles.FirstOrDefault(p => p.Id == a.ProfileId));
        _loading = false;
        ShowEditorPanels();
    }

    private void ShowEditorPanels()
    {
        var type = (ActionType)Math.Max(0, ActionTypeBox.SelectedIndex);
        PanelKey.IsVisible = type == ActionType.Key;
        PanelMouse.IsVisible = type == ActionType.Mouse;
        PanelMacro.IsVisible = type == ActionType.Macro;
        PanelText.IsVisible = type == ActionType.Text;
        PanelLaunch.IsVisible = type == ActionType.Launch;
        PanelKeymap.IsVisible = type == ActionType.KeymapSwitch;
        PanelProfile.IsVisible = type == ActionType.ProfileSwitch;
        bool target = SwitchModeBox.SelectedIndex >= (int)KeymapSwitchMode.Specific;
        KeymapTargetLabel.IsVisible = KeymapTargetBox.IsVisible = target;
        EditorNote.Text = type switch
        {
            ActionType.Default => "Sends the key this button sends out of the box.",
            ActionType.KeymapSwitch when _profile.Keymaps.Count < 2 => "This profile has one keymap. Add another with + above the keypad.",
            ActionType.Macro when _c.Config.Macros.Count == 0 => "No macros yet. Record one in the Macros tab.",
            _ => "",
        };
    }

    private KeyAction ReadEditor()
    {
        var a = new KeyAction { Type = (ActionType)Math.Max(0, ActionTypeBox.SelectedIndex) };
        switch (a.Type)
        {
            case ActionType.Key:
                a.Key = (KeyBox.SelectedItem as KeyDef)?.Id;
                if (ModCtrl.IsChecked == true) a.Modifiers.Add("LCtrl");
                if (ModShift.IsChecked == true) a.Modifiers.Add("LShift");
                if (ModAlt.IsChecked == true) a.Modifiers.Add("LAlt");
                if (ModWin.IsChecked == true) a.Modifiers.Add("LWin");
                break;
            case ActionType.Mouse: a.Mouse = MouseBox.SelectedItem is CoreMouse m ? m : CoreMouse.Left; break;
            case ActionType.Macro:
                a.MacroId = (MacroBox.SelectedItem as Macro)?.Id;
                a.Playback = (MacroPlayback)Math.Max(0, PlaybackBox.SelectedIndex);
                break;
            case ActionType.Text: a.Text = TextValueBox.Text; break;
            case ActionType.Launch: a.Path = PathBox.Text?.Trim(); a.Args = string.IsNullOrWhiteSpace(ArgsBox.Text) ? null : ArgsBox.Text; break;
            case ActionType.KeymapSwitch:
                a.SwitchMode = (KeymapSwitchMode)Math.Max(0, SwitchModeBox.SelectedIndex);
                a.KeymapIndex = Math.Max(0, KeymapTargetBox.SelectedIndex);
                break;
            case ActionType.ProfileSwitch: a.ProfileId = (ProfileTargetBox.SelectedItem as Profile)?.Id; break;
        }
        return a;
    }

    private void ApplyAction(KeyAction? action)
    {
        if (_selectedKey == null) return;
        string? problem = action switch
        {
            { Type: ActionType.Key, Key: null } => "Choose a key first.",
            { Type: ActionType.Macro, MacroId: null } => "Choose a macro first.",
            { Type: ActionType.ProfileSwitch, ProfileId: null } => "Choose a profile first.",
            { Type: ActionType.Launch } when string.IsNullOrWhiteSpace(action.Path) => "Enter a program, file or URL first.",
            _ => null,
        };
        if (problem != null) { EditorNote.Text = problem; return; }
        string key = _selectedKey;
        var keymap = Keymap;
        _c.Engine.Sync(() =>
        {
            if (action == null || action.Type == ActionType.Default) keymap.Bindings.Remove(key);
            else keymap.Bindings[key] = action;
        });
        _c.Save();
        RefreshKeys();
        if (action == null) LoadEditor(new KeyAction());
        EditorNote.Text = "Saved.";
    }

    /// <summary>Maps the key Avalonia reports (by physical position) to our key table.</summary>
    private static KeyDef? FromAvalonia(Avalonia.Input.PhysicalKey key)
    {
        string n = key.ToString();
        if (n.StartsWith("Digit")) n = n[5..];
        else if (n.StartsWith("NumPad"))
            n = n[6..] switch { "Enter" => "NumEnter", "Add" => "NumPlus", "Subtract" => "NumMinus", "Multiply" => "NumStar", "Divide" => "NumSlash", "Decimal" => "NumPeriod", var d => "Num" + d };
        else n = n switch
        {
            "ShiftLeft" => "LShift", "ShiftRight" => "RShift", "ControlLeft" => "LCtrl", "ControlRight" => "RCtrl",
            "AltLeft" => "LAlt", "AltRight" => "RAlt", "MetaLeft" => "LWin", "MetaRight" => "RWin",
            "ArrowUp" => "Up", "ArrowDown" => "Down", "ArrowLeft" => "Left", "ArrowRight" => "Right",
            "Backquote" => "Grave", "BracketLeft" => "LBracket", "BracketRight" => "RBracket", "Equal" => "Equals",
            "AudioVolumeUp" => "VolumeUp", "AudioVolumeDown" => "VolumeDown", "AudioVolumeMute" => "VolumeMute",
            "MediaPlayPause" => "MediaPlayPause", "MediaTrackNext" => "MediaNext", "MediaTrackPrevious" => "MediaPrev",
            _ => n,
        };
        return KeyTable.Get(n);
    }

    // ---------------------------------------------------------------------------------------
    // Macros
    // ---------------------------------------------------------------------------------------
    private void RefreshMacros()
    {
        _loading = true;
        if (_macro != null && !_c.Config.Macros.Contains(_macro)) _macro = null;
        Fill(MacroList, _c.Config.Macros.ToList(), _macro);
        MacroEditor.IsEnabled = _macro != null;
        MacroNameBox.Text = _macro?.Name ?? "";
        _loading = false;
        RefreshSteps(-1);
    }

    private void RefreshSteps(int select)
    {
        Fill(StepList, _macro?.Steps.ToList() ?? new List<MacroStep>(), null);
        if (select >= 0 && _macro != null && select < _macro.Steps.Count) StepList.SelectedIndex = select;
    }

    private void EditSteps(Action<List<MacroStep>> edit, int select = -1)
    {
        if (_macro == null) return;
        var macro = _macro;
        _c.Engine.Sync(() => edit(macro.Steps));
        RefreshSteps(select);
        _c.Save();
    }

    private void SetRecording(bool on)
    {
        if (_macro == null) on = false;
        RecordBtn.IsChecked = on;
        RecordBtn.Content = on ? "■ Stop" : "● Record";
        RecordHint.Text = on ? "Recording — type the sequence on any keyboard. Mapping is paused." : "";
        _recordDown.Clear();
        _recordClock.Reset();
        UpdatePaused();
        if (!on) { _c.Save(); RefreshKeys(); }
    }

    private bool Recording => RecordBtn.IsChecked == true;

    private void UpdatePaused() => _c.Engine.Paused = MappingSwitch.IsChecked != true || Recording || _calibrating >= 0;

    private void RecordKey(KeyDef key, bool down)
    {
        if (_macro == null) return;
        if (down && !_recordDown.Add(key.Id)) return;      // auto-repeat
        if (!down && !_recordDown.Remove(key.Id)) return;
        var steps = new List<MacroStep>();
        if (_recordClock.IsRunning && RecordDelaysBox.IsChecked == true && _recordClock.ElapsedMilliseconds > 0)
            steps.Add(new MacroStep { Kind = MacroStepKind.Delay, DelayMs = (int)_recordClock.ElapsedMilliseconds });
        steps.Add(new MacroStep { Kind = down ? MacroStepKind.KeyDown : MacroStepKind.KeyUp, Key = key.Id });
        _recordClock.Restart();
        var macro = _macro;
        _c.Engine.Sync(() => macro.Steps.AddRange(steps));
        RefreshSteps(macro.Steps.Count - 1);
        StepList.ScrollIntoView(macro.Steps.Count - 1);
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_calibrating >= 0) { e.Handled = true; return; }   // keep Tab/Space from driving the UI mid-calibration
        if (Recording)
        {
            if (FromAvalonia(e.PhysicalKey) is { } k) RecordKey(k, true);
            e.Handled = true;
            return;
        }
        if (ReferenceEquals(e.Source, CaptureBox) || CaptureBox.IsFocused)
        {
            e.Handled = true;
            var key = FromAvalonia(e.PhysicalKey);
            if (key == null || key.IsModifier) return;   // wait for the non-modifier key of the combo
            KeyBox.SelectedItem = key;
            ModCtrl.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            ModShift.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            ModAlt.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            ModWin.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            CaptureBox.Text = ActionText.Describe(ReadEditor(), _c.Config, _selectedKey ?? "");
        }
    }

    private void OnTunnelKeyUp(object? sender, KeyEventArgs e)
    {
        if (_calibrating >= 0) { e.Handled = true; return; }
        if (!Recording) return;
        if (FromAvalonia(e.PhysicalKey) is { } k) RecordKey(k, false);
        e.Handled = true;
    }

    // ---------------------------------------------------------------------------------------
    // Settings and calibration
    // ---------------------------------------------------------------------------------------
    private void LoadSettings()
    {
        _loading = true;
        var s = _c.Config.Settings;
        BackendWinUsb.IsChecked = s.Backend == BackendKind.WinUsb;
        BackendHook.IsChecked = s.Backend == BackendKind.Hook;
        BackendOff.IsChecked = s.Backend == BackendKind.Off;
        AutoSwitchBox.IsChecked = s.AutoSwitch;
        CloseToTrayBox.IsChecked = s.CloseToTray;
        AutostartBox.IsChecked = _c.Autostart;
        _loading = false;
    }

    private void SetBackend(BackendKind kind)
    {
        if (_loading || _c.Config.Settings.Backend == kind) return;
        StopCalibration("Calibration cancelled.");
        _c.Config.Settings.Backend = kind;
        _c.Save();
        _c.RestartBackend();
    }

    private void StartCalibration()
    {
        if (_calibrating >= 0) { StopCalibration("Calibration cancelled. Keys done so far were kept."); return; }
        if (_c.Backend == null) { CalibrateText.Text = "Turn the input engine on first (direct or hook mode, on Windows)."; return; }
        _calibrating = 0;
        CalibrateBtn.Content = "Cancel";
        CalibrateSkipBtn.IsVisible = true;
        UpdatePaused();
        PromptCalibration();
    }

    private void PromptCalibration()
    {
        if (_calibrating >= DeviceLayout.Keys.Count) { StopCalibration("Calibration finished and saved."); return; }
        var k = DeviceLayout.Keys[_calibrating];
        CalibrateText.Text = $"Press {KeyTitle(k)} on the keypad…  ({_calibrating + 1} of {DeviceLayout.Keys.Count})";
    }

    private void OnCalibrationKey(byte usage)
    {
        if (_calibrating < 0 || _calibrating >= DeviceLayout.Keys.Count) return;
        string id = DeviceLayout.Keys[_calibrating].Id;
        _c.Engine.Sync(() =>
        {
            foreach (var other in _c.Config.Layout.Where(p => p.Value == usage && p.Key != id).Select(p => p.Key).ToList()) _c.Config.Layout[other] = 0;
            _c.Config.Layout[id] = usage;
        });
        _calibrating++;
        PromptCalibration();
    }

    private void StopCalibration(string message)
    {
        if (_calibrating < 0) return;
        _calibrating = -1;
        CalibrateBtn.Content = "Start calibration";
        CalibrateSkipBtn.IsVisible = false;
        CalibrateText.Text = message;
        _c.Engine.ReloadLayout();
        UpdatePaused();
        _c.Save();
        RefreshKeys();
    }

    // ---------------------------------------------------------------------------------------
    // Event wiring
    // ---------------------------------------------------------------------------------------
    private void Wire()
    {
        MappingSwitch.IsCheckedChanged += (_, _) => { UpdatePaused(); if (_c.Engine.Paused) _c.Engine.ReleaseAll(); };

        // Profiles
        ProfileList.SelectionChanged += (_, _) => { if (!_loading && ProfileList.SelectedItem is Profile p && !ReferenceEquals(p, _profile)) SelectProfile(p); };
        NewProfileBtn.Click += (_, _) =>
        {
            var p = Profile.CreateDefault(UniqueName("New profile"));
            _c.Engine.Sync(() => _c.Config.Profiles.Add(p));
            SelectProfile(p); _c.Save();
        };
        DuplicateProfileBtn.Click += (_, _) =>
        {
            var p = _profile.Clone(UniqueName(_profile.Name + " copy"));
            _c.Engine.Sync(() => _c.Config.Profiles.Add(p));
            SelectProfile(p); _c.Save();
        };
        DeleteProfileBtn.Click += (_, _) =>
        {
            if (_c.Config.Profiles.Count <= 1) return;
            var doomed = _profile;
            _c.Engine.Sync(() =>
            {
                _c.Config.Profiles.Remove(doomed);
                if (_c.Config.DefaultProfileId == doomed.Id) _c.Config.DefaultProfileId = _c.Config.Profiles[0].Id;
            });
            SelectProfile(_c.Config.DefaultProfile); _c.Save();
        };
        ImportProfileBtn.Click += (_, _) => ImportProfile();
        ExportProfileBtn.Click += (_, _) => ExportProfile();
        ProfileNameBox.LostFocus += (_, _) =>
        {
            var name = ProfileNameBox.Text?.Trim();
            if (_loading || string.IsNullOrEmpty(name) || name == _profile.Name) return;
            _profile.Name = name; RefreshProfiles(); RefreshHeader(); _c.Save();
        };
        DefaultProfileBox.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            if (DefaultProfileBox.IsChecked == true) { _c.Engine.Sync(() => _c.Config.DefaultProfileId = _profile.Id); _c.Save(); }
            else if (_c.Config.DefaultProfileId == _profile.Id) { _loading = true; DefaultProfileBox.IsChecked = true; _loading = false; } // one profile must stay the default
        };
        AddProgramBtn.Click += (_, _) => AddProgram();
        RemoveProgramBtn.Click += (_, _) =>
        {
            if (ProgramList.SelectedItem is not string s) return;
            _c.Engine.Sync(() => _profile.LinkedPrograms.Remove(s)); RefreshProfiles(); _c.Save();
        };

        // Keymaps
        KeymapBox.SelectionChanged += (_, _) =>
        {
            if (_loading || KeymapBox.SelectedIndex < 0) return;
            _keymapIndex = KeymapBox.SelectedIndex;
            if (ReferenceEquals(_c.Engine.Profile, _profile)) _c.Engine.SetKeymap(_keymapIndex);
            RefreshKeymaps();
        };
        AddKeymapBtn.Click += (_, _) =>
        {
            if (_profile.Keymaps.Count >= Profile.MaxKeymaps) return;
            int n = _profile.Keymaps.Count + 1;
            // Give each new keymap a distinct indicator colour combination, like the stock software does.
            var km = new Keymap { Name = "Keymap " + n, LedRed = (n & 2) != 0, LedGreen = (n & 1) != 0, LedBlue = (n & 4) != 0 };
            _c.Engine.Sync(() => _profile.Keymaps.Add(km));
            _keymapIndex = n - 1;
            if (ReferenceEquals(_c.Engine.Profile, _profile)) _c.Engine.SetKeymap(_keymapIndex);
            RefreshKeymaps(); _c.Save();
        };
        RemoveKeymapBtn.Click += (_, _) =>
        {
            if (_profile.Keymaps.Count <= 1) return;
            int index = _keymapIndex;
            _c.Engine.ReleaseAll();
            _c.Engine.Sync(() => _profile.Keymaps.RemoveAt(index));
            _keymapIndex = 0;
            if (ReferenceEquals(_c.Engine.Profile, _profile)) _c.Engine.SetKeymap(0);
            RefreshKeymaps(); _c.Save();
        };
        KeymapNameBox.LostFocus += (_, _) =>
        {
            var name = KeymapNameBox.Text?.Trim();
            if (_loading || string.IsNullOrEmpty(name) || name == Keymap.Name) return;
            Keymap.Name = name; RefreshKeymaps(); RefreshHeader(); _c.Save();
        };
        EventHandler<RoutedEventArgs> led = (_, _) =>
        {
            if (_loading) return;
            var km = Keymap;
            (km.LedRed, km.LedGreen, km.LedBlue) = (LedRBox.IsChecked == true, LedGBox.IsChecked == true, LedBBox.IsChecked == true);
            _c.ApplyLighting(); RefreshHeader(); _c.Save();
        };
        LedRBox.IsCheckedChanged += led; LedGBox.IsCheckedChanged += led; LedBBox.IsCheckedChanged += led;

        // Assignment editor
        ActionTypeBox.SelectionChanged += (_, _) => { if (!_loading) ShowEditorPanels(); };
        SwitchModeBox.SelectionChanged += (_, _) => { if (!_loading) ShowEditorPanels(); };
        ApplyActionBtn.Click += (_, _) => ApplyAction(ReadEditor());
        ResetActionBtn.Click += (_, _) => ApplyAction(null);
        BrowsePathBtn.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose what to launch" });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } p) PathBox.Text = p;
        };

        // Macros
        MacroList.SelectionChanged += (_, _) => { if (_loading) return; SetRecording(false); _macro = MacroList.SelectedItem as Macro; RefreshMacros(); };
        NewMacroBtn.Click += (_, _) =>
        {
            var m = new Macro { Name = "Macro " + (_c.Config.Macros.Count + 1) };
            _c.Engine.Sync(() => _c.Config.Macros.Add(m));
            _macro = m; RefreshMacros(); _c.Save();
        };
        DeleteMacroBtn.Click += (_, _) =>
        {
            if (_macro == null) return;
            SetRecording(false);
            var doomed = _macro;
            _c.Engine.Sync(() => _c.Config.Macros.Remove(doomed));
            _macro = null; RefreshMacros(); RefreshKeys(); _c.Save();
        };
        MacroNameBox.LostFocus += (_, _) =>
        {
            var name = MacroNameBox.Text?.Trim();
            if (_loading || _macro == null || string.IsNullOrEmpty(name) || name == _macro.Name) return;
            _macro.Name = name; RefreshMacros(); RefreshKeys(); _c.Save();
        };
        RecordBtn.Click += (_, _) => SetRecording(RecordBtn.IsChecked == true);
        StepUpBtn.Click += (_, _) => { int i = StepList.SelectedIndex; if (i > 0) EditSteps(s => (s[i - 1], s[i]) = (s[i], s[i - 1]), i - 1); };
        StepDownBtn.Click += (_, _) => { int i = StepList.SelectedIndex; if (_macro != null && i >= 0 && i < _macro.Steps.Count - 1) EditSteps(s => (s[i + 1], s[i]) = (s[i], s[i + 1]), i + 1); };
        DeleteStepBtn.Click += (_, _) => { int i = StepList.SelectedIndex; if (i >= 0) EditSteps(s => s.RemoveAt(i), i); };
        ClearStepsBtn.Click += (_, _) => EditSteps(s => s.Clear());
        AddDelayBtn.Click += (_, _) =>
        {
            if (!int.TryParse(DelayBox.Text, out int ms) || ms < 0 || ms > 600000) return;
            int i = StepList.SelectedIndex;
            if (_macro != null && i >= 0 && _macro.Steps[i].Kind == MacroStepKind.Delay) EditSteps(s => s[i] = new MacroStep { Kind = MacroStepKind.Delay, DelayMs = ms }, i);
            else EditSteps(s => s.Insert(i < 0 ? s.Count : i + 1, new MacroStep { Kind = MacroStepKind.Delay, DelayMs = ms }), i < 0 ? int.MaxValue : i + 1);
        };
        AddClickBtn.Click += (_, _) =>
        {
            var b = StepMouseBox.SelectedItem is CoreMouse m ? m : CoreMouse.Left;
            EditSteps(s => { s.Add(new MacroStep { Kind = MacroStepKind.MouseDown, Mouse = b }); s.Add(new MacroStep { Kind = MacroStepKind.MouseUp, Mouse = b }); });
        };

        // Lighting
        BrightnessSlider.ValueChanged += (_, _) =>
        {
            BrightnessText.Text = ((int)BrightnessSlider.Value).ToString();
            if (!_loading) _profile.Lighting.Brightness = (byte)BrightnessSlider.Value;
        };
        PulsateBox.IsCheckedChanged += (_, _) => { if (!_loading) _profile.Lighting.Pulsate = PulsateBox.IsChecked == true; };
        ApplyLightingBtn.Click += (_, _) => { _c.Save(); _c.ApplyLighting(); };

        // Settings
        BackendWinUsb.IsCheckedChanged += (_, _) => { if (BackendWinUsb.IsChecked == true) SetBackend(BackendKind.WinUsb); };
        BackendHook.IsCheckedChanged += (_, _) => { if (BackendHook.IsChecked == true) SetBackend(BackendKind.Hook); };
        BackendOff.IsCheckedChanged += (_, _) => { if (BackendOff.IsChecked == true) SetBackend(BackendKind.Off); };
        AutoSwitchBox.IsCheckedChanged += (_, _) => { if (!_loading) { _c.Config.Settings.AutoSwitch = AutoSwitchBox.IsChecked == true; _c.Save(); } };
        CloseToTrayBox.IsCheckedChanged += (_, _) => { if (!_loading) { _c.Config.Settings.CloseToTray = CloseToTrayBox.IsChecked == true; _c.Save(); } };
        AutostartBox.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            try { _c.Autostart = AutostartBox.IsChecked == true; }
            catch (Exception ex) { AppendLog("Could not change autostart: " + ex.Message); }
        };
        CalibrateBtn.Click += (_, _) => StartCalibration();
        CalibrateSkipBtn.Click += (_, _) => { if (_calibrating >= 0) { _calibrating++; PromptCalibration(); } };
        ResetLayoutBtn.Click += (_, _) =>
        {
            StopCalibration("");
            _c.Engine.Sync(() => { foreach (var k in DeviceLayout.Keys) _c.Config.Layout[k.Id] = k.DefaultUsage; });
            _c.Engine.ReloadLayout(); _c.Save(); RefreshKeys();
            CalibrateText.Text = "Factory layout restored.";
        };
    }
}
