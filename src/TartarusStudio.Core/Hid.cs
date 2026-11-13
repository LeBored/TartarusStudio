namespace TartarusStudio.Core;

/// <summary>Parses standard 8-byte HID boot keyboard reports and tracks which usages are down.</summary>
public sealed class KeyboardReportTracker
{
    private HashSet<byte> _down = new();

    /// <summary>Returns null when the buffer is not a keyboard report we understand.</summary>
    public static HashSet<byte>? Parse(ReadOnlySpan<byte> report)
    {
        // Some firmwares prefix a report ID (1) to the same 8-byte layout.
        if (report.Length == 9 && report[0] == 1) report = report[1..];
        if (report.Length != 8) return null;
        var set = new HashSet<byte>();
        for (int bit = 0; bit < 8; bit++)
            if ((report[0] & (1 << bit)) != 0) set.Add((byte)(0xE0 + bit));
        for (int i = 2; i < 8; i++)
        {
            byte u = report[i];
            if (u == 1) return null;   // ErrorRollOver: too many keys; keep previous state
            if (u > 3) set.Add(u);
        }
        return set;
    }

    /// <summary>Feeds a report and returns the key transitions it implies (releases first).</summary>
    public List<(byte Usage, bool Down)> Update(ReadOnlySpan<byte> report)
    {
        var changes = new List<(byte, bool)>();
        var now = Parse(report);
        if (now == null) return changes;
        foreach (var u in _down) if (!now.Contains(u)) changes.Add((u, false));
        foreach (var u in now) if (!_down.Contains(u)) changes.Add((u, true));
        _down = now;
        return changes;
    }

    /// <summary>Device went away: everything it held is now up.</summary>
    public List<(byte Usage, bool Down)> Reset()
    {
        var changes = _down.Select(u => (u, false)).ToList();
        _down = new HashSet<byte>();
        return changes;
    }
}

/// <summary>
/// Razer's 90-byte control report, as documented by the OpenRazer project. Only the handful of
/// LED commands the original Tartarus understands are built here.
/// </summary>
public static class RazerProtocol
{
    public const int ReportLength = 90;
    private const byte VarStore = 0x01;
    public const byte LedBacklight = 0x05, LedProfileRed = 0x0C, LedProfileGreen = 0x0D, LedProfileBlue = 0x0E;

    public static byte[] Build(byte commandClass, byte commandId, params byte[] args)
    {
        if (args.Length > 80) throw new ArgumentException("Too many arguments", nameof(args));
        var r = new byte[ReportLength];
        r[1] = 0xFF;                     // transaction id used by older devices
        r[5] = (byte)args.Length;        // data size
        r[6] = commandClass;
        r[7] = commandId;
        args.CopyTo(r, 8);
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= r[i];
        r[88] = crc;
        return r;
    }

    public static byte[] SetLedState(byte led, bool on) => Build(0x03, 0x00, VarStore, led, (byte)(on ? 1 : 0));
    public static byte[] SetLedEffect(byte led, byte effect) => Build(0x03, 0x02, VarStore, led, effect);
    public static byte[] SetLedBrightness(byte led, byte brightness) => Build(0x03, 0x03, VarStore, led, brightness);

    /// <summary>The command sequence that applies a backlight setting.</summary>
    public static IEnumerable<byte[]> Backlight(byte brightness, bool pulsate)
    {
        yield return SetLedState(LedBacklight, brightness > 0);
        yield return SetLedBrightness(LedBacklight, brightness);
        yield return SetLedEffect(LedBacklight, (byte)(pulsate ? 0x02 : 0x00));
    }

    public static IEnumerable<byte[]> ProfileLeds(bool red, bool green, bool blue)
    {
        yield return SetLedState(LedProfileRed, red);
        yield return SetLedState(LedProfileGreen, green);
        yield return SetLedState(LedProfileBlue, blue);
    }
}
