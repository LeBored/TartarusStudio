namespace TartarusStudio.Core;

/// <summary>One keyboard key: HID usage (keyboard page), set-1 scan code, and virtual key.</summary>
public sealed record KeyDef(string Id, string Label, byte Usage, ushort Scan, bool Extended, ushort Vk)
{
    public bool IsModifier => Usage >= 0xE0 && Usage <= 0xE7;
    public override string ToString() => Label;
}

public static class KeyTable
{
    public static readonly IReadOnlyList<KeyDef> All = Build();
    private static readonly Dictionary<string, KeyDef> ById = All.ToDictionary(k => k.Id, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<byte, KeyDef> ByUsageMap = All.Where(k => k.Usage != 0).ToDictionary(k => k.Usage);
    private static readonly Dictionary<int, KeyDef> ByScanMap = All.Where(k => k.Scan != 0).ToDictionary(k => ScanKey(k.Scan, k.Extended));
    private static readonly Dictionary<ushort, KeyDef> ByVkMap = All.GroupBy(k => k.Vk).ToDictionary(g => g.Key, g => g.First());

    private static int ScanKey(ushort scan, bool ext) => scan | (ext ? 0x100 : 0);

    public static KeyDef? Get(string? id) => id != null && ById.TryGetValue(id, out var k) ? k : null;
    public static KeyDef? ByUsage(byte usage) => ByUsageMap.TryGetValue(usage, out var k) ? k : null;
    public static KeyDef? FromScan(ushort scan, bool extended) => ByScanMap.TryGetValue(ScanKey(scan, extended), out var k) ? k : null;
    public static KeyDef? ByVk(ushort vk) => ByVkMap.TryGetValue(vk, out var k) ? k : null;

    private static List<KeyDef> Build()
    {
        var l = new List<KeyDef>();
        void K(string id, string label, byte usage, ushort scan, ushort vk, bool ext = false) => l.Add(new KeyDef(id, label, usage, scan, ext, vk));

        // Letters: scan codes in A..Z order.
        ushort[] letterScan = { 0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C };
        for (int i = 0; i < 26; i++) { var c = ((char)('A' + i)).ToString(); K(c, c, (byte)(0x04 + i), letterScan[i], (ushort)('A' + i)); }
        for (int i = 1; i <= 9; i++) K(i.ToString(), i.ToString(), (byte)(0x1D + i), (ushort)(0x01 + i), (ushort)('0' + i));
        K("0", "0", 0x27, 0x0B, '0');

        K("Enter", "Enter", 0x28, 0x1C, 0x0D); K("Escape", "Esc", 0x29, 0x01, 0x1B); K("Backspace", "Backspace", 0x2A, 0x0E, 0x08);
        K("Tab", "Tab", 0x2B, 0x0F, 0x09); K("Space", "Space", 0x2C, 0x39, 0x20);
        K("Minus", "-", 0x2D, 0x0C, 0xBD); K("Equals", "=", 0x2E, 0x0D, 0xBB); K("LBracket", "[", 0x2F, 0x1A, 0xDB); K("RBracket", "]", 0x30, 0x1B, 0xDD);
        K("Backslash", "\\", 0x31, 0x2B, 0xDC); K("Semicolon", ";", 0x33, 0x27, 0xBA); K("Quote", "'", 0x34, 0x28, 0xDE); K("Grave", "`", 0x35, 0x29, 0xC0);
        K("Comma", ",", 0x36, 0x33, 0xBC); K("Period", ".", 0x37, 0x34, 0xBE); K("Slash", "/", 0x38, 0x35, 0xBF); K("CapsLock", "Caps Lock", 0x39, 0x3A, 0x14);

        for (int i = 0; i < 10; i++) K("F" + (i + 1), "F" + (i + 1), (byte)(0x3A + i), (ushort)(0x3B + i), (ushort)(0x70 + i));
        K("F11", "F11", 0x44, 0x57, 0x7A); K("F12", "F12", 0x45, 0x58, 0x7B);

        K("PrintScreen", "Print Screen", 0x46, 0x37, 0x2C, true); K("ScrollLock", "Scroll Lock", 0x47, 0x46, 0x91);
        K("Insert", "Insert", 0x49, 0x52, 0x2D, true); K("Home", "Home", 0x4A, 0x47, 0x24, true); K("PageUp", "Page Up", 0x4B, 0x49, 0x21, true);
        K("Delete", "Delete", 0x4C, 0x53, 0x2E, true); K("End", "End", 0x4D, 0x4F, 0x23, true); K("PageDown", "Page Down", 0x4E, 0x51, 0x22, true);
        K("Right", "Right", 0x4F, 0x4D, 0x27, true); K("Left", "Left", 0x50, 0x4B, 0x25, true); K("Down", "Down", 0x51, 0x50, 0x28, true); K("Up", "Up", 0x52, 0x48, 0x26, true);

        K("NumSlash", "Num /", 0x54, 0x35, 0x6F, true); K("NumStar", "Num *", 0x55, 0x37, 0x6A); K("NumMinus", "Num -", 0x56, 0x4A, 0x6D); K("NumPlus", "Num +", 0x57, 0x4E, 0x6B);
        K("NumEnter", "Num Enter", 0x58, 0x1C, 0x0D, true);
        ushort[] numScan = { 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49 };
        for (int i = 1; i <= 9; i++) K("Num" + i, "Num " + i, (byte)(0x58 + i), numScan[i - 1], (ushort)(0x60 + i));
        K("Num0", "Num 0", 0x62, 0x52, 0x60); K("NumPeriod", "Num .", 0x63, 0x53, 0x6E);

        K("LCtrl", "Left Ctrl", 0xE0, 0x1D, 0xA2); K("LShift", "Left Shift", 0xE1, 0x2A, 0xA0); K("LAlt", "Left Alt", 0xE2, 0x38, 0xA4); K("LWin", "Left Win", 0xE3, 0x5B, 0x5B, true);
        K("RCtrl", "Right Ctrl", 0xE4, 0x1D, 0xA3, true); K("RShift", "Right Shift", 0xE5, 0x36, 0xA1); K("RAlt", "Right Alt", 0xE6, 0x38, 0xA5, true); K("RWin", "Right Win", 0xE7, 0x5C, 0x5C, true);

        // Media keys have no keyboard-page usage or scan code; they are sent by virtual key.
        K("VolumeUp", "Volume Up", 0, 0, 0xAF); K("VolumeDown", "Volume Down", 0, 0, 0xAE); K("VolumeMute", "Mute", 0, 0, 0xAD);
        K("MediaPlayPause", "Play/Pause", 0, 0, 0xB3); K("MediaNext", "Next Track", 0, 0, 0xB0); K("MediaPrev", "Previous Track", 0, 0, 0xB1);
        return l;
    }
}

/// <summary>A physical control on the Tartarus and where to draw it.</summary>
public sealed record PhysicalKey(string Id, string Label, byte DefaultUsage, double X, double Y, double W, double H);

public static class DeviceLayout
{
    public const int RazerVendorId = 0x1532;
    public const int TartarusProductId = 0x0201; // original Tartarus (RZ07-0103)

    public static readonly IReadOnlyList<PhysicalKey> Keys = Build();

    public static PhysicalKey? Get(string id) => Keys.FirstOrDefault(k => k.Id == id);

    public static Dictionary<string, byte> DefaultUsages() => Keys.ToDictionary(k => k.Id, k => k.DefaultUsage);

    private static List<PhysicalKey> Build()
    {
        var l = new List<PhysicalKey>();
        // Factory key output of the keypad without any software: a slice of the left side of a QWERTY board.
        byte[] defaults = { 0x2B, 0x14, 0x1A, 0x08, 0x15, /* Tab Q W E R */ 0x39, 0x04, 0x16, 0x07, 0x09, /* Caps A S D F */ 0xE1, 0x1D, 0x1B, 0x06, 0x19 /* LShift Z X C V */ };
        for (int i = 0; i < 15; i++)
        {
            int row = i / 5, col = i % 5;
            l.Add(new PhysicalKey($"K{i + 1:00}", $"{i + 1:00}", defaults[i], 20 + col * 74, 24 + row * 76, 68, 68));
        }
        l.Add(new PhysicalKey("ThumbButton", "16", 0xE2, 460, 150, 64, 44));   // Left Alt
        l.Add(new PhysicalKey("DUp", "▲", 0x52, 470, 214, 44, 38));
        l.Add(new PhysicalKey("DLeft", "◀", 0x50, 422, 256, 44, 38));
        l.Add(new PhysicalKey("DRight", "▶", 0x4F, 518, 256, 44, 38));
        l.Add(new PhysicalKey("DDown", "▼", 0x51, 470, 298, 44, 38));
        l.Add(new PhysicalKey("ThumbKey", "␣", 0x2C, 412, 352, 150, 40));     // Space
        return l;
    }
}
