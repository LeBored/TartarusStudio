using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using TartarusStudio.Core;

namespace TartarusStudio.App;

internal sealed class NullOutput : IOutput
{
    public void KeyDown(KeyDef key) { }
    public void KeyUp(KeyDef key) { }
    public void MouseButton(MouseAction button, bool down) { }
    public void Wheel(int clicks) { }
    public void TypeText(string text) { }
    public void Launch(string path, string? args) { }
}

/// <summary>Owns the config, the mapping engine, the input backend and the device lighting.</summary>
internal sealed class Controller : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "TartarusStudio";
    private readonly RazerControl? _razer;
    private string? _lastForeground;
    private bool _lightingWarned;

    public Controller()
    {
        ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TartarusStudio", "config.json");
        Config = ConfigStore.Load(ConfigPath);
        IOutput output = new NullOutput();
        if (IsWindows)
        {
            var send = new SendInputOutput();
            send.Error += m => Log?.Invoke(m);
            output = send;
            _razer = new RazerControl(Config.Settings.VendorId, Config.Settings.ProductId);
            _razer.Log += m => Log?.Invoke(m);
        }
        Engine = new MappingEngine(output, Config);
        Engine.StateChanged += () => { ApplyLighting(); StateChanged?.Invoke(); };
        Engine.ProfileSwitchRequested += id =>
        {
            if (Config.Profiles.FirstOrDefault(p => p.Id == id) is { } p) ActivateProfile(p);
        };
    }

    public static bool IsWindows => OperatingSystem.IsWindows();
    public string ConfigPath { get; }
    public AppConfig Config { get; }
    public MappingEngine Engine { get; }
    public IInputBackend? Backend { get; private set; }

    /// <summary>Raised on arbitrary threads.</summary>
    public event Action<string>? Log;
    public event Action? StateChanged;
    public event Action<byte>? DeviceKeyObserved;

    public string BackendStatus => !IsWindows ? "Input engine runs on Windows only."
        : Backend?.Status ?? "Input engine is off.";

    public void Save()
    {
        try { Engine.Sync(() => ConfigStore.Save(ConfigPath, Config)); }
        catch (Exception ex) { Log?.Invoke("Could not save settings: " + ex.Message); }
    }

    public void RestartBackend()
    {
        Backend?.Dispose();
        Backend = null;
        Engine.ReleaseAll();
        if (!IsWindows) return;
        var s = Config.Settings;
        Backend = s.Backend switch
        {
            BackendKind.WinUsb => new WinUsbBackend(s.VendorId, s.ProductId),
            BackendKind.Hook => new HookBackend(s.VendorId, s.ProductId, Engine.Owns, s.HookWaitMs),
            _ => null,
        };
        Engine.SoftwareRepeat = s.Backend == BackendKind.WinUsb;
        if (Backend == null) { Log?.Invoke("Input engine off."); return; }
        Backend.Log += m => Log?.Invoke(m);
        Backend.KeyEvent += Engine.OnUsage;
        Backend.Observed += u => DeviceKeyObserved?.Invoke(u);
        Backend.Start();
        Log?.Invoke($"Input engine started: {(s.Backend == BackendKind.WinUsb ? "direct (WinUSB)" : "hook")} mode.");
        ApplyLighting();
    }

    public void ActivateProfile(Profile p) => Engine.SetProfile(p); // StateChanged applies lighting

    /// <summary>Pushes backlight and keymap LEDs for the active profile to the device.</summary>
    public void ApplyLighting()
    {
        if (_razer == null) return;
        var profile = Engine.Profile;
        var keymap = Engine.ActiveKeymap;
        var (brightness, pulsate) = (profile.Lighting.Brightness, profile.Lighting.Pulsate);
        var (r, g, b) = (keymap.LedRed, keymap.LedGreen, keymap.LedBlue);
        Task.Run(() =>
        {
            bool ok = _razer.Send(RazerProtocol.Backlight(brightness, pulsate).Concat(RazerProtocol.ProfileLeds(r, g, b)));
            if (!ok && !_lightingWarned) { _lightingWarned = true; Log?.Invoke("Lighting: the keypad did not accept the control report (not connected, or no control interface available)."); }
            if (ok) _lightingWarned = false;
        });
    }

    /// <summary>Call periodically: switches profile when the foreground program changes.</summary>
    public void ForegroundTick()
    {
        if (!IsWindows || !Config.Settings.AutoSwitch) { _lastForeground = null; return; }
        var exe = ForegroundExe();
        if (exe == null || exe == _lastForeground) return;
        _lastForeground = exe;
        if (string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return; // keep editing in peace
        var target = ProfileResolver.Resolve(Config, exe);
        if (!ReferenceEquals(target, Engine.Profile))
        {
            Log?.Invoke($"{Path.GetFileName(exe)} → profile \"{target.Name}\".");
            ActivateProfile(target);
        }
    }

    private static string? ForegroundExe()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        var process = Native.OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new char[1024];
            uint size = (uint)name.Length;
            return Native.QueryFullProcessImageName(process, 0, name, ref size) ? new string(name, 0, (int)size) : null;
        }
        finally { Native.CloseHandle(process); }
    }

    public bool Autostart
    {
        get
        {
            if (!IsWindows) return false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) != null;
        }
        set
        {
            if (!IsWindows) return;
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue(RunName, false);
        }
    }

    public void Dispose()
    {
        Backend?.Dispose();
        Engine.Dispose();
    }
}
