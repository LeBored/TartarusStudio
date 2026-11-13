using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TartarusStudio.Core;

namespace TartarusStudio.App;

/// <summary>Sends mapped keys with SendInput, as scan codes so games that read scan codes see them.</summary>
internal sealed class SendInputOutput : IOutput
{
    private static readonly int Size = Marshal.SizeOf<Native.INPUT>();
    public event Action<string>? Error;

    private static Native.INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = Native.InjectTag } },
    };

    private static void SendKey(KeyDef k, bool up)
    {
        uint flags = up ? Native.KEYEVENTF_KEYUP : 0;
        Native.INPUT input;
        if (k.Scan != 0)
            input = Key(0, k.Scan, flags | Native.KEYEVENTF_SCANCODE | (k.Extended ? Native.KEYEVENTF_EXTENDEDKEY : 0));
        else
            input = Key(k.Vk, 0, flags | Native.KEYEVENTF_EXTENDEDKEY); // media keys
        Native.SendInput(1, new[] { input }, Size);
    }

    public void KeyDown(KeyDef key) => SendKey(key, false);
    public void KeyUp(KeyDef key) => SendKey(key, true);

    public void MouseButton(MouseAction button, bool down)
    {
        (uint flag, uint data) = button switch
        {
            MouseAction.Left => (down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP, 0u),
            MouseAction.Right => (down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP, 0u),
            MouseAction.Middle => (down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP, 0u),
            MouseAction.Back => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, 1u),
            MouseAction.Forward => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, 2u),
            _ => (0u, 0u),
        };
        if (flag != 0) SendMouse(flag, data);
    }

    public void Wheel(int clicks) => SendMouse(Native.MOUSEEVENTF_WHEEL, unchecked((uint)(clicks * 120)));

    private static void SendMouse(uint flags, uint data)
    {
        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            u = new Native.InputUnion { mi = new Native.MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = Native.InjectTag } },
        };
        Native.SendInput(1, new[] { input }, Size);
    }

    public void TypeText(string text)
    {
        var inputs = new Native.INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = Key(0, text[i], Native.KEYEVENTF_UNICODE);
            inputs[i * 2 + 1] = Key(0, text[i], Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP);
        }
        if (inputs.Length > 0) Native.SendInput((uint)inputs.Length, inputs, Size);
    }

    public void Launch(string path, string? args)
    {
        try { Process.Start(new ProcessStartInfo(path, args ?? "") { UseShellExecute = true }); }
        catch (Exception ex) { Error?.Invoke($"Could not launch \"{path}\": {ex.Message}"); }
    }
}
