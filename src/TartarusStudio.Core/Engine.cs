using System.Collections.Concurrent;

namespace TartarusStudio.Core;

/// <summary>Where mapped actions go. The Windows implementation uses SendInput.</summary>
public interface IOutput
{
    void KeyDown(KeyDef key);
    void KeyUp(KeyDef key);
    void MouseButton(MouseAction button, bool down);
    void Wheel(int clicks);
    void TypeText(string text);
    void Launch(string path, string? args);
}

public sealed class MacroPlayer
{
    private readonly IOutput _out;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public MacroPlayer(IOutput output) => _out = output;

    public bool IsRunning(string token) => _running.ContainsKey(token);

    public Task Start(string token, Macro macro, bool loop)
    {
        Stop(token);
        var cts = new CancellationTokenSource();
        _running[token] = cts;
        var steps = macro.Steps.ToArray(); // snapshot: the editor may change the macro while it plays
        return Task.Run(() => Run(token, steps, loop, cts));
    }

    public void Stop(string token)
    {
        if (_running.TryRemove(token, out var cts)) cts.Cancel();
    }

    public void StopAll()
    {
        foreach (var token in _running.Keys.ToArray()) Stop(token);
    }

    private async Task Run(string token, MacroStep[] steps, bool loop, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var keysDown = new List<KeyDef>();
        var mouseDown = new List<MouseAction>();
        try
        {
            do
            {
                foreach (var s in steps)
                {
                    ct.ThrowIfCancellationRequested();
                    switch (s.Kind)
                    {
                        case MacroStepKind.KeyDown:
                            if (KeyTable.Get(s.Key) is { } kd) { _out.KeyDown(kd); keysDown.Add(kd); }
                            break;
                        case MacroStepKind.KeyUp:
                            if (KeyTable.Get(s.Key) is { } ku) { _out.KeyUp(ku); keysDown.Remove(ku); }
                            break;
                        case MacroStepKind.MouseDown: _out.MouseButton(s.Mouse, true); mouseDown.Add(s.Mouse); break;
                        case MacroStepKind.MouseUp: _out.MouseButton(s.Mouse, false); mouseDown.Remove(s.Mouse); break;
                        case MacroStepKind.Text: if (!string.IsNullOrEmpty(s.Text)) _out.TypeText(s.Text); break;
                        case MacroStepKind.Delay: if (s.DelayMs > 0) await Task.Delay(s.DelayMs, ct).ConfigureAwait(false); break;
                    }
                }
                // Never let a looping macro without delays spin a core or flood the input queue.
                if (loop) await Task.Delay(10, ct).ConfigureAwait(false);
            } while (loop);
        }
        catch (OperationCanceledException) { }
        finally
        {
            for (int i = keysDown.Count - 1; i >= 0; i--) _out.KeyUp(keysDown[i]);
            foreach (var m in mouseDown) _out.MouseButton(m, false);
            _running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(token, cts));
            cts.Dispose();
        }
    }
}

/// <summary>
/// Turns "physical Tartarus key went down/up" into output according to the active profile and keymap.
/// Thread-safe: backends call it from their own threads.
/// </summary>
public sealed class MappingEngine : IDisposable
{
    private sealed class Held
    {
        public required KeyAction Action;
        public KeyDef? Main;
        public List<KeyDef> Mods = new();
        public int PrevKeymap = -1;
    }

    private readonly object _gate = new();
    private readonly IOutput _out;
    private readonly MacroPlayer _macros;
    private readonly Dictionary<string, Held> _held = new();
    private readonly Timer _repeatTimer;
    private Dictionary<byte, string> _byUsage = new();
    private AppConfig _cfg;
    private Profile _profile;
    private int _keymap;
    private string? _repeatKey;

    public MappingEngine(IOutput output, AppConfig config)
    {
        _out = output;
        _macros = new MacroPlayer(output);
        _cfg = config;
        _profile = config.DefaultProfile;
        _repeatTimer = new Timer(_ => RepeatTick());
        ReloadLayout();
    }

    /// <summary>While true the engine ignores the device (used during calibration and when mapping is off).</summary>
    public bool Paused { get; set; }

    /// <summary>Generate auto-repeat ourselves. Needed when reading the device directly, where Windows does no typematic repeat.</summary>
    public bool SoftwareRepeat { get; set; }

    public event Action? StateChanged;
    public event Action<Guid>? ProfileSwitchRequested;

    public Profile Profile { get { lock (_gate) return _profile; } }
    public int KeymapIndex { get { lock (_gate) return _keymap; } }
    public Keymap ActiveKeymap { get { lock (_gate) return _profile.Keymaps[Math.Clamp(_keymap, 0, _profile.Keymaps.Count - 1)]; } }

    /// <summary>Run an edit of the config/profile under the engine lock so input threads never see a half-made change.</summary>
    public void Sync(Action edit) { lock (_gate) edit(); }

    public void ReloadLayout()
    {
        lock (_gate)
        {
            var map = new Dictionary<byte, string>();
            foreach (var (key, usage) in _cfg.Layout)
                if (usage != 0) map[usage] = key;
            _byUsage = map;
        }
    }

    public void SetConfig(AppConfig cfg)
    {
        lock (_gate) { ReleaseAllLocked(); _cfg = cfg; _profile = cfg.DefaultProfile; _keymap = 0; }
        ReloadLayout();
        StateChanged?.Invoke();
    }

    public void SetProfile(Profile profile)
    {
        lock (_gate)
        {
            if (ReferenceEquals(profile, _profile)) return;
            ReleaseAllLocked();
            _profile = profile;
            _keymap = 0;
        }
        StateChanged?.Invoke();
    }

    public void SetKeymap(int index)
    {
        lock (_gate) _keymap = Math.Clamp(index, 0, _profile.Keymaps.Count - 1);
        StateChanged?.Invoke();
    }

    public string? KeyForUsage(byte usage) { lock (_gate) return _byUsage.GetValueOrDefault(usage); }

    /// <summary>True when the engine wants this usage from the device (hook mode uses it to decide what to swallow).</summary>
    public bool Owns(byte usage) => !Paused && KeyForUsage(usage) != null;

    public void OnUsage(byte usage, bool down)
    {
        var key = KeyForUsage(usage);
        if (key != null) OnPhysical(key, down);
    }

    public void OnPhysical(string key, bool down)
    {
        Action? after = null;
        lock (_gate)
        {
            if (Paused) return;
            if (down)
            {
                if (_held.TryGetValue(key, out var already))
                {
                    // Hardware auto-repeat (hook mode): forward it for plain key bindings only.
                    if (!SoftwareRepeat && already.Main is { IsModifier: false } m) _out.KeyDown(m);
                    return;
                }
                var keymap = _profile.Keymaps[Math.Clamp(_keymap, 0, _profile.Keymaps.Count - 1)];
                var action = keymap.Bindings.GetValueOrDefault(key) ?? new KeyAction();
                var held = new Held { Action = action };
                _held[key] = held;
                after = Press(key, held);
            }
            else
            {
                if (!_held.Remove(key, out var held)) return;
                after = Release(key, held);
            }
        }
        after?.Invoke();
    }

    private Action? Press(string key, Held h)
    {
        var a = h.Action;
        switch (a.Type)
        {
            case ActionType.Default:
                h.Main = _cfg.Layout.TryGetValue(key, out var u) ? KeyTable.ByUsage(u) : null;
                if (h.Main != null) { _out.KeyDown(h.Main); ArmRepeat(key, h.Main); }
                break;
            case ActionType.Key:
                foreach (var id in a.Modifiers)
                    if (KeyTable.Get(id) is { } mod) { _out.KeyDown(mod); h.Mods.Add(mod); }
                h.Main = KeyTable.Get(a.Key);
                if (h.Main != null) { _out.KeyDown(h.Main); ArmRepeat(key, h.Main); }
                break;
            case ActionType.Mouse:
                if (a.Mouse == MouseAction.WheelUp) _out.Wheel(1);
                else if (a.Mouse == MouseAction.WheelDown) _out.Wheel(-1);
                else _out.MouseButton(a.Mouse, true);
                break;
            case ActionType.Macro:
                var macro = _cfg.Macros.FirstOrDefault(m => m.Id == a.MacroId);
                if (macro == null) break;
                if (a.Playback == MacroPlayback.Toggle)
                {
                    var token = "toggle:" + macro.Id;
                    if (_macros.IsRunning(token)) _macros.Stop(token); else _macros.Start(token, macro, loop: true);
                }
                else _macros.Start("key:" + key, macro, loop: a.Playback == MacroPlayback.WhileHeld);
                break;
            case ActionType.Text:
                if (!string.IsNullOrEmpty(a.Text)) _out.TypeText(a.Text);
                break;
            case ActionType.Launch:
                if (!string.IsNullOrWhiteSpace(a.Path)) { var (p, args) = (a.Path, a.Args); return () => _out.Launch(p, args); }
                break;
            case ActionType.KeymapSwitch:
                int n = _profile.Keymaps.Count;
                if (a.SwitchMode == KeymapSwitchMode.HoldSpecific) h.PrevKeymap = _keymap;
                _keymap = a.SwitchMode switch
                {
                    KeymapSwitchMode.Next => (_keymap + 1) % n,
                    KeymapSwitchMode.Previous => (_keymap + n - 1) % n,
                    _ => Math.Clamp(a.KeymapIndex, 0, n - 1),
                };
                return () => StateChanged?.Invoke();
            case ActionType.ProfileSwitch:
                if (a.ProfileId is { } id2) return () => ProfileSwitchRequested?.Invoke(id2);
                break;
        }
        return null;
    }

    private Action? Release(string key, Held h)
    {
        if (_repeatKey == key) _repeatKey = null;
        var a = h.Action;
        switch (a.Type)
        {
            case ActionType.Default:
            case ActionType.Key:
                if (h.Main != null) _out.KeyUp(h.Main);
                for (int i = h.Mods.Count - 1; i >= 0; i--) _out.KeyUp(h.Mods[i]);
                break;
            case ActionType.Mouse:
                if (a.Mouse != MouseAction.WheelUp && a.Mouse != MouseAction.WheelDown) _out.MouseButton(a.Mouse, false);
                break;
            case ActionType.Macro:
                if (a.Playback == MacroPlayback.WhileHeld) _macros.Stop("key:" + key);
                break;
            case ActionType.KeymapSwitch:
                if (a.SwitchMode == KeymapSwitchMode.HoldSpecific && h.PrevKeymap >= 0)
                {
                    _keymap = Math.Clamp(h.PrevKeymap, 0, _profile.Keymaps.Count - 1);
                    return () => StateChanged?.Invoke();
                }
                break;
        }
        return null;
    }

    private void ArmRepeat(string key, KeyDef main)
    {
        if (!SoftwareRepeat || main.IsModifier) return;
        _repeatKey = key;
        _repeatTimer.Change(_cfg.Settings.RepeatDelayMs, Math.Max(10, _cfg.Settings.RepeatRateMs));
    }

    private void RepeatTick()
    {
        lock (_gate)
        {
            if (_repeatKey != null && _held.TryGetValue(_repeatKey, out var h) && h.Main != null) _out.KeyDown(h.Main);
            else _repeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>Let go of everything we are holding down on the user's behalf.</summary>
    public void ReleaseAll() { lock (_gate) ReleaseAllLocked(); }

    private void ReleaseAllLocked()
    {
        foreach (var (key, h) in _held.ToArray()) Release(key, h);
        _held.Clear();
        _repeatKey = null;
        _macros.StopAll();
    }

    public void Dispose()
    {
        ReleaseAll();
        _repeatTimer.Dispose();
    }
}
