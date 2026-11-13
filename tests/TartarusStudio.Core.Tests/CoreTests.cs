using TartarusStudio.Core;
using Xunit;

namespace TartarusStudio.Core.Tests;

sealed class FakeOutput : IOutput
{
    public readonly List<string> Log = new();
    void Add(string s) { lock (Log) Log.Add(s); }
    public void KeyDown(KeyDef key) => Add("+" + key.Id);
    public void KeyUp(KeyDef key) => Add("-" + key.Id);
    public void MouseButton(MouseAction b, bool down) => Add((down ? "+M" : "-M") + b);
    public void Wheel(int clicks) => Add("W" + clicks);
    public void TypeText(string text) => Add("T:" + text);
    public void Launch(string path, string? args) => Add("L:" + path);
    public string[] Snapshot() { lock (Log) return Log.ToArray(); }
}

public class EngineTests
{
    static (MappingEngine, FakeOutput, AppConfig) Make()
    {
        var cfg = new AppConfig(); cfg.Normalize();
        var o = new FakeOutput();
        return (new MappingEngine(o, cfg), o, cfg);
    }

    [Fact]
    public void Unbound_key_sends_its_factory_key()
    {
        var (e, o, _) = Make();
        e.OnUsage(0x14, true); e.OnUsage(0x14, false); // K02 = Q
        Assert.Equal(new[] { "+Q", "-Q" }, o.Snapshot());
    }

    [Fact]
    public void Key_binding_presses_modifiers_first_and_releases_in_reverse()
    {
        var (e, o, cfg) = Make();
        cfg.Profiles[0].Keymaps[0].Bindings["K01"] = new KeyAction { Type = ActionType.Key, Key = "F5", Modifiers = { "LCtrl", "LShift" } };
        e.OnPhysical("K01", true); e.OnPhysical("K01", false);
        Assert.Equal(new[] { "+LCtrl", "+LShift", "+F5", "-F5", "-LShift", "-LCtrl" }, o.Snapshot());
    }

    [Fact]
    public void Usages_the_device_does_not_emit_are_not_owned()
    {
        var (e, o, _) = Make();
        Assert.True(e.Owns(0x2B));   // Tab = K01
        Assert.False(e.Owns(0x0A));  // G
        e.OnUsage(0x0A, true);
        Assert.Empty(o.Snapshot());
        e.Paused = true;
        Assert.False(e.Owns(0x2B));
    }

    [Fact]
    public void Hold_keymap_applies_while_held_and_release_uses_the_action_that_was_pressed()
    {
        var (e, o, cfg) = Make();
        var p = cfg.Profiles[0];
        p.Keymaps.Add(new Keymap { Name = "Alt" });
        p.Keymaps[0].Bindings["ThumbButton"] = new KeyAction { Type = ActionType.KeymapSwitch, SwitchMode = KeymapSwitchMode.HoldSpecific, KeymapIndex = 1 };
        p.Keymaps[1].Bindings["K02"] = new KeyAction { Type = ActionType.Key, Key = "1" };
        int changes = 0; e.StateChanged += () => changes++;

        e.OnPhysical("ThumbButton", true);
        Assert.Equal(1, e.KeymapIndex);
        e.OnPhysical("K02", true);
        e.OnPhysical("ThumbButton", false);
        Assert.Equal(0, e.KeymapIndex);
        e.OnPhysical("K02", false);            // must release "1", not Q
        Assert.Equal(new[] { "+1", "-1" }, o.Snapshot());
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Next_keymap_wraps_around()
    {
        var (e, _, cfg) = Make();
        cfg.Profiles[0].Keymaps.Add(new Keymap());
        cfg.Profiles[0].Keymaps[0].Bindings["K15"] = new KeyAction { Type = ActionType.KeymapSwitch, SwitchMode = KeymapSwitchMode.Next };
        cfg.Profiles[0].Keymaps[1].Bindings["K15"] = new KeyAction { Type = ActionType.KeymapSwitch, SwitchMode = KeymapSwitchMode.Next };
        e.OnPhysical("K15", true); e.OnPhysical("K15", false);
        Assert.Equal(1, e.KeymapIndex);
        e.OnPhysical("K15", true); e.OnPhysical("K15", false);
        Assert.Equal(0, e.KeymapIndex);
    }

    [Fact]
    public void Switching_profile_releases_held_keys()
    {
        var (e, o, cfg) = Make();
        var other = Profile.CreateDefault("Other"); cfg.Profiles.Add(other);
        e.OnPhysical("K03", true);
        e.SetProfile(other);
        Assert.Equal(new[] { "+W", "-W" }, o.Snapshot());
        e.OnPhysical("K03", false);            // stale release is ignored
        Assert.Equal(2, o.Snapshot().Length);
    }

    [Fact]
    public void Hardware_repeat_is_forwarded_for_keys_but_not_for_other_actions()
    {
        var (e, o, cfg) = Make();
        cfg.Profiles[0].Keymaps[0].Bindings["K04"] = new KeyAction { Type = ActionType.Text, Text = "gg" };
        e.OnPhysical("K02", true); e.OnPhysical("K02", true);
        e.OnPhysical("K04", true); e.OnPhysical("K04", true);
        Assert.Equal(new[] { "+Q", "+Q", "T:gg" }, o.Snapshot());
    }

    [Fact]
    public void Mouse_and_wheel_bindings()
    {
        var (e, o, cfg) = Make();
        var b = cfg.Profiles[0].Keymaps[0].Bindings;
        b["K01"] = new KeyAction { Type = ActionType.Mouse, Mouse = MouseAction.Right };
        b["K02"] = new KeyAction { Type = ActionType.Mouse, Mouse = MouseAction.WheelDown };
        b["K03"] = new KeyAction { Type = ActionType.Disabled };
        foreach (var k in new[] { "K01", "K02", "K03" }) { e.OnPhysical(k, true); e.OnPhysical(k, false); }
        Assert.Equal(new[] { "+MRight", "-MRight", "W-1" }, o.Snapshot());
    }

    [Fact]
    public async Task Macro_plays_once_and_releases_stuck_keys_when_cancelled()
    {
        var o = new FakeOutput();
        var player = new MacroPlayer(o);
        var m = new Macro { Steps = { new() { Kind = MacroStepKind.KeyDown, Key = "A" }, new() { Kind = MacroStepKind.KeyUp, Key = "A" }, new() { Kind = MacroStepKind.Text, Text = "x" } } };
        await player.Start("t", m, loop: false);
        Assert.Equal(new[] { "+A", "-A", "T:x" }, o.Snapshot());
        Assert.False(player.IsRunning("t"));

        var o2 = new FakeOutput();
        var p2 = new MacroPlayer(o2);
        var hold = new Macro { Steps = { new() { Kind = MacroStepKind.KeyDown, Key = "B" }, new() { Kind = MacroStepKind.Delay, DelayMs = 60_000 } } };
        var task = p2.Start("h", hold, loop: true);
        for (int i = 0; i < 200 && o2.Snapshot().Length == 0; i++) await Task.Delay(5);
        p2.Stop("h");
        await task;
        Assert.Equal(new[] { "+B", "-B" }, o2.Snapshot());
    }

    [Fact]
    public async Task Toggle_macro_starts_on_first_press_and_stops_on_second()
    {
        var (e, o, cfg) = Make();
        var m = new Macro { Steps = { new() { Kind = MacroStepKind.KeyDown, Key = "E" }, new() { Kind = MacroStepKind.Delay, DelayMs = 60_000 } } };
        cfg.Macros.Add(m);
        cfg.Profiles[0].Keymaps[0].Bindings["K05"] = new KeyAction { Type = ActionType.Macro, MacroId = m.Id, Playback = MacroPlayback.Toggle };
        e.OnPhysical("K05", true); e.OnPhysical("K05", false);
        for (int i = 0; i < 200 && o.Snapshot().Length == 0; i++) await Task.Delay(5);
        Assert.Equal(new[] { "+E" }, o.Snapshot());
        e.OnPhysical("K05", true); e.OnPhysical("K05", false);
        for (int i = 0; i < 200 && o.Snapshot().Length < 2; i++) await Task.Delay(5);
        Assert.Equal(new[] { "+E", "-E" }, o.Snapshot());
    }
}

public class HidTests
{
    [Fact]
    public void Tracker_reports_transitions_between_boot_reports()
    {
        var t = new KeyboardReportTracker();
        Assert.Equal(new[] { ((byte)0xE1, true), ((byte)0x1A, true) }, t.Update(new byte[] { 0x02, 0, 0x1A, 0, 0, 0, 0, 0 }));
        Assert.Equal(new[] { ((byte)0xE1, false), ((byte)0x04, true) }, t.Update(new byte[] { 0, 0, 0x1A, 0x04, 0, 0, 0, 0 }));
        Assert.Empty(t.Update(new byte[] { 0, 0, 1, 1, 1, 1, 1, 1 }));           // rollover error keeps state
        Assert.Empty(t.Update(new byte[] { 4, 0, 0, 0 }));                        // unknown report shape
        Assert.Equal(2, t.Reset().Count);
    }

    [Fact]
    public void Report_id_prefixed_keyboard_report_is_accepted()
    {
        var set = KeyboardReportTracker.Parse(new byte[] { 1, 0x04, 0, 0x2C, 0, 0, 0, 0, 0 });
        Assert.NotNull(set);
        Assert.True(set!.SetEquals(new byte[] { 0xE2, 0x2C }));
    }

    [Fact]
    public void Razer_report_has_expected_framing_and_checksum()
    {
        var r = RazerProtocol.SetLedBrightness(RazerProtocol.LedBacklight, 0x80);
        Assert.Equal(90, r.Length);
        Assert.Equal(0xFF, r[1]);
        Assert.Equal(new byte[] { 3, 0x03, 0x03, 0x01, 0x05, 0x80 }, r[5..11]);
        Assert.Equal(0x03 ^ 0x03 ^ 0x03 ^ 0x01 ^ 0x05 ^ 0x80, r[88]);
        Assert.Equal(0, r[89]);
    }

    [Fact]
    public void Key_table_is_consistent()
    {
        Assert.Equal("W", KeyTable.FromScan(0x11, false)!.Id);
        Assert.Equal("Up", KeyTable.FromScan(0x48, true)!.Id);
        Assert.Equal("Num8", KeyTable.FromScan(0x48, false)!.Id);
        Assert.Equal("9", KeyTable.ByUsage(0x26)!.Id);
        Assert.Null(KeyTable.FromScan(0x2A, true)); // fake shift that brackets extended keys
        foreach (var k in DeviceLayout.Keys) Assert.NotNull(KeyTable.ByUsage(k.DefaultUsage));
        Assert.Equal(21, DeviceLayout.Keys.Count);
    }
}

public class StoreTests
{
    [Fact]
    public void Config_round_trips_and_normalizes()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "config.json");
        var cfg = ConfigStore.Load(path);
        Assert.Single(cfg.Profiles);
        cfg.Profiles[0].Keymaps[0].Bindings["K01"] = new KeyAction { Type = ActionType.Key, Key = "F1", Modifiers = { "LAlt" } };
        cfg.Layout["K15"] = 0x2C;
        ConfigStore.Save(path, cfg);
        var back = ConfigStore.Load(path);
        var a = back.Profiles[0].Keymaps[0].Bindings["K01"];
        Assert.Equal((ActionType.Key, "F1", "LAlt"), (a.Type, a.Key, a.Modifiers[0]));
        Assert.Equal(0x2C, back.Layout["K15"]);
        Assert.Equal(cfg.DefaultProfileId, back.DefaultProfileId);

        File.WriteAllText(path, "{ not json");
        Assert.Single(ConfigStore.Load(path).Profiles);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Resolver_matches_exe_name_case_insensitively_and_prefers_full_path()
    {
        var cfg = new AppConfig(); cfg.Normalize();
        var byName = Profile.CreateDefault("ByName"); byName.LinkedPrograms.Add("Game.exe");
        var byPath = Profile.CreateDefault("ByPath"); byPath.LinkedPrograms.Add(@"D:\Beta\game.exe");
        cfg.Profiles.Add(byName); cfg.Profiles.Add(byPath);
        Assert.Same(byName, ProfileResolver.Resolve(cfg, @"C:\Games\GAME.EXE"));
        Assert.Same(byPath, ProfileResolver.Resolve(cfg, @"D:\Beta\game.exe"));
        Assert.Same(cfg.Profiles[0], ProfileResolver.Resolve(cfg, @"C:\Windows\notepad.exe"));
        Assert.Same(cfg.Profiles[0], ProfileResolver.Resolve(cfg, null));
    }

    [Fact]
    public void Imported_profile_gets_a_new_id()
    {
        var p = Profile.CreateDefault("X");
        var q = ConfigStore.ImportProfile(ConfigStore.ExportProfile(p));
        Assert.NotEqual(p.Id, q.Id);
        Assert.Equal("X", q.Name);
    }
}
