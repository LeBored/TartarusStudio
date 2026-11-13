using System.Text.Json.Serialization;

namespace TartarusStudio.Core;

public enum ActionType { Default, Key, Mouse, Macro, Text, Launch, KeymapSwitch, ProfileSwitch, Disabled }
public enum MouseAction { Left, Right, Middle, Back, Forward, WheelUp, WheelDown }
public enum MacroPlayback { Once, WhileHeld, Toggle }
public enum KeymapSwitchMode { Next, Previous, Specific, HoldSpecific }
public enum MacroStepKind { KeyDown, KeyUp, Delay, MouseDown, MouseUp, Text }
public enum BackendKind { WinUsb, Hook, Off }

public sealed class KeyAction
{
    public ActionType Type { get; set; } = ActionType.Default;
    public string? Key { get; set; }
    public List<string> Modifiers { get; set; } = new();
    public MouseAction Mouse { get; set; }
    public Guid? MacroId { get; set; }
    public MacroPlayback Playback { get; set; }
    public string? Text { get; set; }
    public string? Path { get; set; }
    public string? Args { get; set; }
    public KeymapSwitchMode SwitchMode { get; set; }
    public int KeymapIndex { get; set; }
    public Guid? ProfileId { get; set; }

    public KeyAction Clone()
    {
        var c = (KeyAction)MemberwiseClone();
        c.Modifiers = new List<string>(Modifiers);
        return c;
    }
}

public sealed class Keymap
{
    public string Name { get; set; } = "Keymap";
    public bool LedRed { get; set; }
    public bool LedGreen { get; set; }
    public bool LedBlue { get; set; }
    public Dictionary<string, KeyAction> Bindings { get; set; } = new();

    public Keymap Clone() => new()
    {
        Name = Name, LedRed = LedRed, LedGreen = LedGreen, LedBlue = LedBlue,
        Bindings = Bindings.ToDictionary(p => p.Key, p => p.Value.Clone()),
    };
}

public sealed class Lighting
{
    public byte Brightness { get; set; } = 255;
    public bool Pulsate { get; set; }
}

public sealed class Profile
{
    public const int MaxKeymaps = 8;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Profile";
    /// <summary>Executable names (e.g. "witcher3.exe") or full paths that activate this profile.</summary>
    public List<string> LinkedPrograms { get; set; } = new();
    public List<Keymap> Keymaps { get; set; } = new();
    public Lighting Lighting { get; set; } = new();

    public override string ToString() => Name;

    public Profile Clone(string newName) => new()
    {
        Name = newName,
        Keymaps = Keymaps.Select(k => k.Clone()).ToList(),
        Lighting = new Lighting { Brightness = Lighting.Brightness, Pulsate = Lighting.Pulsate },
    };

    public static Profile CreateDefault(string name)
    {
        var p = new Profile { Name = name };
        p.Keymaps.Add(new Keymap { Name = "Keymap 1", LedGreen = true });
        return p;
    }
}

public sealed class MacroStep
{
    public MacroStepKind Kind { get; set; }
    public string? Key { get; set; }
    public MouseAction Mouse { get; set; }
    public int DelayMs { get; set; }
    public string? Text { get; set; }

    public override string ToString() => Kind switch
    {
        MacroStepKind.KeyDown => $"↓  {KeyTable.Get(Key)?.Label ?? Key}",
        MacroStepKind.KeyUp => $"↑  {KeyTable.Get(Key)?.Label ?? Key}",
        MacroStepKind.Delay => $"⏱  {DelayMs} ms",
        MacroStepKind.MouseDown => $"↓  Mouse {Mouse}",
        MacroStepKind.MouseUp => $"↑  Mouse {Mouse}",
        MacroStepKind.Text => $"✎  \"{Text}\"",
        _ => Kind.ToString(),
    };
}

public sealed class Macro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Macro";
    public List<MacroStep> Steps { get; set; } = new();
    public override string ToString() => Name;
}

public sealed class Settings
{
    public BackendKind Backend { get; set; } = BackendKind.WinUsb;
    public bool AutoSwitch { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public int VendorId { get; set; } = DeviceLayout.RazerVendorId;
    public int ProductId { get; set; } = DeviceLayout.TartarusProductId;
    /// <summary>Hook mode: how long the keyboard hook waits for Raw Input to say which device a key came from.</summary>
    public int HookWaitMs { get; set; } = 4;
    public int RepeatDelayMs { get; set; } = 400;
    public int RepeatRateMs { get; set; } = 33;
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    public Guid DefaultProfileId { get; set; }
    public List<Profile> Profiles { get; set; } = new();
    public List<Macro> Macros { get; set; } = new();
    /// <summary>Physical key id → HID usage the device emits for it. Calibration rewrites this.</summary>
    public Dictionary<string, byte> Layout { get; set; } = DeviceLayout.DefaultUsages();

    [JsonIgnore] public Profile DefaultProfile => Profiles.FirstOrDefault(p => p.Id == DefaultProfileId) ?? Profiles[0];

    public void Normalize()
    {
        if (Profiles.Count == 0) Profiles.Add(Profile.CreateDefault("Default"));
        foreach (var p in Profiles)
            if (p.Keymaps.Count == 0) p.Keymaps.Add(new Keymap { Name = "Keymap 1", LedGreen = true });
        if (Profiles.All(p => p.Id != DefaultProfileId)) DefaultProfileId = Profiles[0].Id;
        foreach (var k in DeviceLayout.Keys)
            if (!Layout.ContainsKey(k.Id)) Layout[k.Id] = k.DefaultUsage;
    }
}

public static class ActionText
{
    public static string Describe(KeyAction? a, AppConfig cfg, string physicalKeyId)
    {
        if (a == null || a.Type == ActionType.Default)
            return cfg.Layout.TryGetValue(physicalKeyId, out var u) ? KeyTable.ByUsage(u)?.Label ?? "—" : "—";
        switch (a.Type)
        {
            case ActionType.Key:
                var parts = a.Modifiers.Select(m => ShortMod(m)).ToList();
                parts.Add(KeyTable.Get(a.Key)?.Label ?? "?");
                return string.Join("+", parts);
            case ActionType.Mouse: return "Mouse " + a.Mouse;
            case ActionType.Macro: return "▶ " + (cfg.Macros.FirstOrDefault(m => m.Id == a.MacroId)?.Name ?? "(missing)");
            case ActionType.Text: return "✎ " + Trim(a.Text);
            case ActionType.Launch: return "⧉ " + Trim(System.IO.Path.GetFileNameWithoutExtension(a.Path ?? ""));
            case ActionType.KeymapSwitch:
                return a.SwitchMode switch
                {
                    KeymapSwitchMode.Next => "Keymap ▸",
                    KeymapSwitchMode.Previous => "Keymap ◂",
                    KeymapSwitchMode.HoldSpecific => $"Hold: map {a.KeymapIndex + 1}",
                    _ => $"Map {a.KeymapIndex + 1}",
                };
            case ActionType.ProfileSwitch: return "⇄ " + (cfg.Profiles.FirstOrDefault(p => p.Id == a.ProfileId)?.Name ?? "(missing)");
            case ActionType.Disabled: return "Disabled";
            default: return a.Type.ToString();
        }
    }

    private static string ShortMod(string id) => id switch
    {
        "LCtrl" or "RCtrl" => "Ctrl", "LShift" or "RShift" => "Shift", "LAlt" or "RAlt" => "Alt", "LWin" or "RWin" => "Win", _ => id,
    };

    private static string Trim(string? s) => string.IsNullOrEmpty(s) ? "" : s.Length <= 12 ? s : s[..11] + "…";
}
