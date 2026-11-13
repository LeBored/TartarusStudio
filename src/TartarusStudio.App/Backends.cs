using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using TartarusStudio.Core;

namespace TartarusStudio.App;

/// <summary>A way of getting key events from the Tartarus, and only the Tartarus.</summary>
internal interface IInputBackend : IDisposable
{
    /// <summary>A key on the device changed and should be mapped.</summary>
    event Action<byte, bool>? KeyEvent;
    /// <summary>A key went down on the device, mapped or not (used by calibration).</summary>
    event Action<byte>? Observed;
    event Action<string>? Log;
    string Status { get; }
    void Start();
}

internal static class DeviceId
{
    public static string Tag(int vid, int pid) => $"VID_{vid:X4}&PID_{pid:X4}";
}

// =============================================================================================
// Direct mode: the keypad's keyboard interface is bound to Microsoft's in-box WinUSB driver, so
// Windows no longer treats it as a keyboard and we read its HID reports ourselves.
// =============================================================================================
internal sealed class WinUsbBackend : IInputBackend
{
    private readonly int _vid, _pid;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private volatile string _status = "Starting…";

    public WinUsbBackend(int vid, int pid) { _vid = vid; _pid = pid; }

    public event Action<byte, bool>? KeyEvent;
    public event Action<byte>? Observed;
    public event Action<string>? Log;
    public string Status => _status;

    public void Start()
    {
        _thread = new Thread(Supervise) { IsBackground = true, Name = "WinUSB supervisor" };
        _thread.Start();
    }

    /// <summary>Finds device nodes of the keypad whose driver is WinUSB and returns their WinUSB interface paths.</summary>
    public static List<(string InstanceId, string Path)> FindWinUsbPaths(int vid, int pid, Action<string>? log = null)
    {
        var result = new List<(string, string)>();
        string tag = DeviceId.Tag(vid, pid);
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb == null) return result;
        foreach (var hw in usb.GetSubKeyNames().Where(n => n.StartsWith(tag, StringComparison.OrdinalIgnoreCase)))
        {
            using var hwKey = usb.OpenSubKey(hw);
            if (hwKey == null) continue;
            foreach (var inst in hwKey.GetSubKeyNames())
            {
                using var instKey = hwKey.OpenSubKey(inst);
                var service = instKey?.GetValue("Service") as string;
                if (!string.Equals(service, "WinUSB", StringComparison.OrdinalIgnoreCase)) continue;
                using var dp = instKey!.OpenSubKey("Device Parameters");
                var guids = new List<string>();
                if (dp?.GetValue("DeviceInterfaceGUIDs") is string[] multi) guids.AddRange(multi);
                if (dp?.GetValue("DeviceInterfaceGUID") is string single) guids.Add(single);
                // Windows' own Winusb.inf (driver picked by hand in Device Manager) exposes this fixed interface GUID.
                guids.Add("{dee824ef-729b-4a0e-9c14-b7117d33a817}");
                string instanceId = $@"USB\{hw}\{inst}";
                foreach (var g in guids.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Guid.TryParse(g, out var guid)) continue;
                    foreach (var path in Native.InterfacePaths(guid, instanceId)) result.Add((instanceId, path));
                }
            }
        }
        return result;
    }

    private void Supervise()
    {
        var ct = _cts.Token;
        bool announcedMissing = false;
        while (!ct.IsCancellationRequested)
        {
            var paths = FindWinUsbPaths(_vid, _pid, m => Log?.Invoke(m));
            if (paths.Count == 0)
            {
                _status = "Direct mode: no WinUSB interface found. See Settings → Setup.";
                if (!announcedMissing) { Log?.Invoke($"No present WinUSB interface for {DeviceId.Tag(_vid, _pid)}. Is the keypad plugged in and the driver replaced with Zadig?"); announcedMissing = true; }
                ct.WaitHandle.WaitOne(2000);
                continue;
            }
            announcedMissing = false;
            var readers = new List<Thread>();
            var open = new List<(SafeFileHandle File, IntPtr Usb)>();
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            foreach (var (instance, path) in paths)
            {
                var file = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (file.IsInvalid) { Log?.Invoke($"Open failed for {instance} (error {Marshal.GetLastWin32Error()}). Another program may be using it."); continue; }
                if (!Native.WinUsb_Initialize(file, out var usb)) { Log?.Invoke($"WinUsb_Initialize failed for {instance} (error {Marshal.GetLastWin32Error()})."); file.Dispose(); continue; }
                open.Add((file, usb));
                // If the whole composite device (not one interface) was given to WinUSB, the other interfaces hang off the first.
                var handles = new List<IntPtr> { usb };
                for (byte i = 0; i < 4; i++)
                    if (Native.WinUsb_GetAssociatedInterface(usb, i, out var assoc)) { handles.Add(assoc); open.Add((file, assoc)); }
                foreach (var h in handles)
                {
                    if (!Native.WinUsb_QueryInterfaceSettings(h, 0, out var desc)) continue;
                    for (byte p = 0; p < desc.bNumEndpoints; p++)
                    {
                        if (!Native.WinUsb_QueryPipe(h, 0, p, out var pipe)) continue;
                        if (pipe.PipeType != 3 || (pipe.PipeId & 0x80) == 0) continue; // interrupt IN only
                        uint timeout = 500;
                        Native.WinUsb_SetPipePolicy(h, pipe.PipeId, Native.PIPE_TRANSFER_TIMEOUT, 4, ref timeout);
                        Log?.Invoke($"Reading interface {desc.bInterfaceNumber}, endpoint 0x{pipe.PipeId:X2} ({pipe.MaximumPacketSize} bytes).");
                        var (hh, pid, size, ifNum) = (h, pipe.PipeId, pipe.MaximumPacketSize, desc.bInterfaceNumber);
                        var t = new Thread(() => ReadLoop(hh, pid, size, ifNum, session)) { IsBackground = true, Name = $"WinUSB if{ifNum}", Priority = ThreadPriority.AboveNormal };
                        readers.Add(t);
                        t.Start();
                    }
                }
            }
            if (readers.Count == 0)
            {
                _status = "Direct mode: device found but it could not be opened.";
                Close(open);
                ct.WaitHandle.WaitOne(2000);
                continue;
            }
            _status = "Direct mode: connected.";
            foreach (var t in readers) t.Join();
            Close(open);
            if (!ct.IsCancellationRequested) { _status = "Direct mode: keypad disconnected."; Log?.Invoke("Keypad disconnected; waiting for it to return."); ct.WaitHandle.WaitOne(1000); }
        }
        _status = "Stopped.";
    }

    private static void Close(List<(SafeFileHandle File, IntPtr Usb)> open)
    {
        foreach (var (_, usb) in open.AsEnumerable().Reverse()) Native.WinUsb_Free(usb);
        foreach (var file in open.Select(o => o.File).Distinct()) file.Dispose();
    }

    private void ReadLoop(IntPtr usb, byte pipe, int packetSize, byte interfaceNumber, CancellationTokenSource session)
    {
        var tracker = new KeyboardReportTracker();
        var buffer = new byte[Math.Max(packetSize, 8)];
        int unknownLogged = 0;
        try
        {
            while (!session.IsCancellationRequested)
            {
                if (!Native.WinUsb_ReadPipe(usb, pipe, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == Native.ERROR_SEM_TIMEOUT) continue;      // idle; loop to notice cancellation
                    if (!session.IsCancellationRequested) Log?.Invoke($"Read on interface {interfaceNumber} ended (error {err}).");
                    session.Cancel();                                    // unplugged: tear the session down and rescan
                    break;
                }
                if (read == 0) continue;
                var report = buffer.AsSpan(0, (int)read);
                if (KeyboardReportTracker.Parse(report) == null && unknownLogged < 20)
                {
                    unknownLogged++;
                    Log?.Invoke($"Interface {interfaceNumber}: unrecognised report {Convert.ToHexString(report)}");
                }
                foreach (var (usage, down) in tracker.Update(report)) Emit(usage, down);
            }
        }
        finally
        {
            foreach (var (usage, down) in tracker.Reset()) Emit(usage, down);
        }
    }

    private void Emit(byte usage, bool down)
    {
        if (down) Observed?.Invoke(usage);
        KeyEvent?.Invoke(usage, down);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(3000);
    }
}

// =============================================================================================
// Hook mode: no driver change. A low-level keyboard hook cannot see which keyboard a key came
// from, and Raw Input (which can) is delivered only after the hook has answered. So the hook
// holds back every key the keypad could have produced, and when Raw Input names the device the
// key is either mapped (keypad) or replayed unchanged (any other keyboard).
// =============================================================================================
internal sealed class HookBackend : IInputBackend
{
    private readonly record struct RawRecord(ushort Scan, bool Extended, bool Up, bool FromDevice, long Ticks);
    private readonly record struct Pending(ushort Scan, bool Extended, bool Up, byte Usage, long Ticks);

    // The window class outlives any one backend, so its procedure must be static and never collected.
    private static HookBackend? s_current;
    private static readonly Native.WndProc s_wndProc = (hwnd, msg, wParam, lParam) =>
    {
        if (msg == Native.WM_INPUT) s_current?.OnRawInput(lParam);
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    };

    private readonly string _tag;
    private readonly Func<byte, bool> _wanted;
    private readonly long _timeoutTicks;
    private readonly object _gate = new();
    private readonly List<RawRecord> _records = new();   // device reports that arrived before their hook call
    private readonly List<Pending> _pending = new();     // held keys waiting for their device report
    private readonly Dictionary<IntPtr, bool> _deviceCache = new();
    private readonly HashSet<byte> _deviceDown = new();
    private Thread? _rawThread, _hookThread;
    private Timer? _sweeper;
    private uint _rawThreadId, _hookThreadId;
    private IntPtr _hook;
    private Native.LowLevelProc? _proc; // keep the delegate alive
    private long _fromKeypad, _fromOther, _timedOut;
    private volatile bool _gaveUp;
    private volatile string _status = "Starting…";

    public HookBackend(int vid, int pid, Func<byte, bool> wanted, int waitMs)
    {
        _tag = DeviceId.Tag(vid, pid);
        _wanted = wanted;
        _timeoutTicks = Stopwatch.Frequency * Math.Clamp(waitMs, 30, 200) / 1000;
    }

    public event Action<byte, bool>? KeyEvent;
    public event Action<byte>? Observed;
    public event Action<string>? Log;

    public string Status
    {
        get
        {
            long k = Interlocked.Read(ref _fromKeypad), o = Interlocked.Read(ref _fromOther), t = Interlocked.Read(ref _timedOut);
            return k + o + t == 0 ? _status : $"{_status} Keystrokes: {k} from keypad, {o} from other keyboards, {t} unidentified.";
        }
    }

    public void Start()
    {
        _rawThread = new Thread(RawLoop) { IsBackground = true, Name = "Raw input", Priority = ThreadPriority.Highest };
        _rawThread.Start();
        _hookThread = new Thread(HookLoop) { IsBackground = true, Name = "Keyboard hook", Priority = ThreadPriority.Highest };
        _hookThread.Start();
        _sweeper = new Timer(_ => Sweep(), null, 10, 10);
    }

    private static void Pump()
    {
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    private void RawLoop()
    {
        _rawThreadId = Native.GetCurrentThreadId();
        s_current = this;
        IntPtr window = Native.CreateMessageWindow("TartarusStudioRawInput", s_wndProc);
        if (window == IntPtr.Zero) { Log?.Invoke($"Could not create the Raw Input window (error {Marshal.GetLastWin32Error()})."); return; }
        var rid = new[] { new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = window } };
        if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            Log?.Invoke($"RegisterRawInputDevices failed (error {Marshal.GetLastWin32Error()}).");
        Pump();
        Native.DestroyWindow(window);
        if (ReferenceEquals(s_current, this)) s_current = null;
    }

    private void HookLoop()
    {
        _hookThreadId = Native.GetCurrentThreadId();
        _proc = HookProc;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { _status = "Hook mode: could not install the keyboard hook."; Log?.Invoke($"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()})."); return; }
        _status = "Hook mode: active.";
        Pump();
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private bool IsOurDevice(IntPtr hDevice)
    {
        if (_deviceCache.TryGetValue(hDevice, out bool known)) return known;
        uint size = 0;
        Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, null, ref size);
        bool ours = false;
        if (size > 0)
        {
            var name = new char[size];
            if (Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, name, ref size) != uint.MaxValue)
                ours = new string(name).Contains(_tag, StringComparison.OrdinalIgnoreCase);
        }
        _deviceCache[hDevice] = ours;
        return ours;
    }

    /// <summary>Acts on a held key once its source is known. Never call while holding the lock.</summary>
    private void Resolve(Pending p, bool ours)
    {
        if (ours)
        {
            Interlocked.Increment(ref _fromKeypad);
            KeyEvent?.Invoke(p.Usage, !p.Up);
        }
        else Replay(p);
    }

    /// <summary>Puts a held key from another keyboard back into the input stream exactly as it was.</summary>
    private static void Replay(Pending p)
    {
        uint flags = Native.KEYEVENTF_SCANCODE | (p.Extended ? Native.KEYEVENTF_EXTENDEDKEY : 0) | (p.Up ? Native.KEYEVENTF_KEYUP : 0);
        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wScan = p.Scan, dwFlags = flags, dwExtraInfo = Native.InjectTag } },
        };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }

    private void OnRawInput(IntPtr hRawInput)
    {
        uint size = (uint)Marshal.SizeOf<Native.RAWINPUTKEYBOARD>();
        if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, out var data, ref size, (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>()) == uint.MaxValue) return;
        if (data.header.dwType != Native.RIM_TYPEKEYBOARD || data.header.hDevice == IntPtr.Zero) return; // no device = injected input
        bool ours = IsOurDevice(data.header.hDevice);
        ushort scan = data.keyboard.MakeCode;
        bool up = (data.keyboard.Flags & Native.RI_KEY_BREAK) != 0, ext = (data.keyboard.Flags & Native.RI_KEY_E0) != 0;
        var key = KeyTable.FromScan(scan, ext);
        if (ours && !up && key is { Usage: not 0 }) Observed?.Invoke(key.Usage);

        Pending? match = null;
        lock (_gate)
        {
            int i = _pending.FindIndex(p => p.Scan == scan && p.Extended == ext && p.Up == up);
            if (i >= 0)
            {
                match = _pending[i];
                _pending.RemoveAt(i);
                if (ours) { if (up) _deviceDown.Remove(match.Value.Usage); else _deviceDown.Add(match.Value.Usage); }
            }
            else
            {
                long now = Stopwatch.GetTimestamp();
                _records.RemoveAll(r => now - r.Ticks > _timeoutTicks);
                _records.Add(new RawRecord(scan, ext, up, ours, now));
            }
        }
        if (match is { } m)
        {
            if (!ours) Interlocked.Increment(ref _fromOther);
            Resolve(m, ours);
        }
    }

    /// <summary>Releases held keys whose device report never came, so no keystroke is ever lost.</summary>
    private void Sweep()
    {
        List<Pending>? expired = null;
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            while (_pending.Count > 0 && now - _pending[0].Ticks > _timeoutTicks)
            {
                (expired ??= new()).Add(_pending[0]);
                _pending.RemoveAt(0);
            }
        }
        if (expired == null) return;
        foreach (var p in expired)
        {
            Interlocked.Increment(ref _timedOut);
            bool finishOurs;
            lock (_gate) finishOurs = p.Up && _deviceDown.Remove(p.Usage); // complete a press we already mapped
            if (finishOurs) KeyEvent?.Invoke(p.Usage, false); else Replay(p);
        }
        if (!_gaveUp && Interlocked.Read(ref _timedOut) >= 20 && Interlocked.Read(ref _fromKeypad) + Interlocked.Read(ref _fromOther) == 0)
        {
            _gaveUp = true; // Windows is not reporting devices for held keys here; stop delaying the user's typing
            _status = "Hook mode cannot identify the keypad on this system; keys pass through unmapped. Use Direct mode.";
            Log?.Invoke(_status);
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _gaveUp) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        if ((k.flags & Native.LLKHF_INJECTED) != 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        bool ext = (k.flags & Native.LLKHF_EXTENDED) != 0, up = (k.flags & Native.LLKHF_UP) != 0;
        ushort scan = (ushort)k.scanCode;
        var key = KeyTable.FromScan(scan, ext);
        if (key == null || key.Usage == 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        bool? known = null;
        lock (_gate)
        {
            // Only keys the keypad can produce are ever held; the rest of the keyboard is untouched.
            if (!_deviceDown.Contains(key.Usage) && !_wanted(key.Usage)) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            long now = Stopwatch.GetTimestamp();
            int i = _records.FindIndex(r => r.Scan == scan && r.Extended == ext && r.Up == up && now - r.Ticks <= _timeoutTicks);
            if (i >= 0)
            {
                // Some systems deliver the device report first; then there is nothing to wait for.
                known = _records[i].FromDevice;
                _records.RemoveAt(i);
                if (known == true) { if (up) _deviceDown.Remove(key.Usage); else _deviceDown.Add(key.Usage); }
            }
            else _pending.Add(new Pending(scan, ext, up, key.Usage, now));
        }
        if (known == false) { Interlocked.Increment(ref _fromOther); return Native.CallNextHookEx(_hook, nCode, wParam, lParam); }
        if (known == true) { Interlocked.Increment(ref _fromKeypad); KeyEvent?.Invoke(key.Usage, !up); }
        return new IntPtr(1);
    }

    public void Dispose()
    {
        _sweeper?.Dispose();
        if (_hookThreadId != 0) Native.PostThreadMessage(_hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (_rawThreadId != 0) Native.PostThreadMessage(_rawThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread?.Join(2000);
        _rawThread?.Join(2000);
        List<Pending> rest;
        lock (_gate) { rest = _pending.ToList(); _pending.Clear(); }
        foreach (var p in rest) Replay(p);
    }
}

/// <summary>Sends Razer control reports (backlight, keymap LEDs) through whichever HID collection accepts them.</summary>
internal sealed class RazerControl
{
    private readonly object _gate = new();
    private readonly string _tag;
    private string? _goodPath;
    public event Action<string>? Log;

    public RazerControl(int vid, int pid) => _tag = DeviceId.Tag(vid, pid);

    public bool Send(IEnumerable<byte[]> reports)
    {
        lock (_gate)
        {
            bool all = true;
            foreach (var r in reports) { all &= SendOne(r); Thread.Sleep(5); }
            return all;
        }
    }

    private bool SendOne(byte[] report)
    {
        var buffer = new byte[report.Length + 1]; // leading report id 0
        report.CopyTo(buffer, 1);
        Native.HidD_GetHidGuid(out var hidGuid);
        var candidates = Native.InterfacePaths(hidGuid).Where(p => p.Contains(_tag, StringComparison.OrdinalIgnoreCase)).ToList();
        if (_goodPath != null && candidates.Remove(_goodPath)) candidates.Insert(0, _goodPath);
        foreach (var path in candidates)
        {
            // Access 0: Windows owns keyboard/mouse collections exclusively, but feature reports still work.
            using var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            if (Native.HidD_SetFeature(h, buffer, (uint)buffer.Length))
            {
                if (_goodPath != path) { _goodPath = path; Log?.Invoke("Lighting control channel found."); }
                return true;
            }
        }
        _goodPath = null;
        return false;
    }
}
