using System.Runtime.InteropServices;

namespace BubblesBot.Bot.Input;

/// <summary>
/// Win32 SendInput / SetCursorPos. Wrapped here so the rest of the Bot project never sees
/// raw P/Invoke. Only InputRouter calls these — behaviors go through <see cref="IInputRouter"/>.
/// </summary>
internal static class SendInputNative
{
    public static void MoveCursor(int absX, int absY)
        => SetCursorPos(absX, absY);

    public static void LeftClick()
    {
        Span<INPUT> inputs = stackalloc INPUT[2];
        inputs[0] = MouseInput(MOUSEEVENTF_LEFTDOWN);
        inputs[1] = MouseInput(MOUSEEVENTF_LEFTUP);
        Send(inputs);
    }

    public static void LeftDrag(int endAbsX, int endAbsY)
    {
        Span<INPUT> down = stackalloc INPUT[1];
        down[0] = MouseInput(MOUSEEVENTF_LEFTDOWN);
        Send(down);
        MoveCursor(endAbsX, endAbsY);
        // Give PoE one small input slice to observe motion while the button is held.
        Thread.Sleep(45);
        Span<INPUT> up = stackalloc INPUT[1];
        up[0] = MouseInput(MOUSEEVENTF_LEFTUP);
        Send(up);
    }

    /// <summary>
    /// VK codes 0x01-0x06 are mouse buttons (LBUTTON/RBUTTON/MBUTTON/XBUTTON1/XBUTTON2). PoE
    /// accepts them as skill bindings and the SendInput keyboard channel doesn't deliver
    /// them — they need MOUSEINPUT events instead. KeyDown/KeyUp/KeyTap dispatch by VK so
    /// the rest of the bot can treat any binding uniformly.
    /// </summary>
    public static void KeyDown(int vk)
    {
        Span<INPUT> inputs = stackalloc INPUT[1];
        inputs[0] = IsMouseButton(vk) ? MouseButton(vk, down: true) : KeyInput((ushort)vk, keyUp: false);
        Send(inputs);
    }

    public static void KeyUp(int vk)
    {
        Span<INPUT> inputs = stackalloc INPUT[1];
        inputs[0] = IsMouseButton(vk) ? MouseButton(vk, down: false) : KeyInput((ushort)vk, keyUp: true);
        Send(inputs);
    }

    public static void KeyTap(int vk)
    {
        Span<INPUT> inputs = stackalloc INPUT[2];
        if (IsMouseButton(vk))
        {
            inputs[0] = MouseButton(vk, down: true);
            inputs[1] = MouseButton(vk, down: false);
        }
        else
        {
            inputs[0] = KeyInput((ushort)vk, keyUp: false);
            inputs[1] = KeyInput((ushort)vk, keyUp: true);
        }
        Send(inputs);
    }

    /// <summary>Hold each modifier down, tap <paramref name="vk"/>, then release modifiers in
    /// reverse order (e.g. Ctrl+Left/Right to step the active stash tab). Sent as SCAN CODES with
    /// real inter-event timing: PoE's stash-tab hotkeys ignore synthetic virtual-key events (same
    /// reason <see cref="ScanCodeTap"/> exists) and need the modifier established before the tap.</summary>
    public static void ModifierKeyTap(int[] modifiers, int vk)
    {
        for (var i = 0; i < modifiers.Length; i++) { SendScan(modifiers[i], down: true); Thread.Sleep(15); }
        SendScan(vk, down: true);
        Thread.Sleep(15);
        SendScan(vk, down: false);
        Thread.Sleep(15);
        for (var i = modifiers.Length - 1; i >= 0; i--) { SendScan(modifiers[i], down: false); Thread.Sleep(10); }
    }

    private static void SendScan(int vk, bool down)
    {
        var scan = (ushort)MapVirtualKeyW((uint)vk, 0 /* MAPVK_VK_TO_VSC */);
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.U.ki.wScan = scan;
        var flags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP);
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        i.U.ki.dwFlags = flags;
        Span<INPUT> inputs = stackalloc INPUT[1];
        inputs[0] = i;
        Send(inputs);
    }

    /// <summary>Tap a hardware scan code. Some PoE system-layer controls consume DirectInput/
    /// scan-code events while ignoring an otherwise equivalent synthetic virtual-key event.</summary>
    public static void ScanCodeTap(int scanCode)
    {
        if (scanCode is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(scanCode));
        Span<INPUT> inputs = stackalloc INPUT[2];
        inputs[0] = ScanCodeInput((ushort)scanCode, keyUp: false);
        inputs[1] = ScanCodeInput((ushort)scanCode, keyUp: true);
        Send(inputs);
    }

    private static bool IsMouseButton(int vk) => vk is 0x01 or 0x02 or 0x04 or 0x05 or 0x06;

    private static INPUT MouseButton(int vk, bool down) => vk switch
    {
        0x01 => MouseInput(down ? MOUSEEVENTF_LEFTDOWN   : MOUSEEVENTF_LEFTUP),
        0x02 => MouseInput(down ? MOUSEEVENTF_RIGHTDOWN  : MOUSEEVENTF_RIGHTUP),
        0x04 => MouseInput(down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP),
        0x05 => MouseInputXButton(down, XBUTTON1),
        0x06 => MouseInputXButton(down, XBUTTON2),
        _    => default,
    };

    private static INPUT MouseInputXButton(bool down, uint xButton)
    {
        var i = new INPUT { type = INPUT_MOUSE };
        i.U.mi.dwFlags   = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
        i.U.mi.mouseData = xButton;
        return i;
    }

    private static unsafe void Send(Span<INPUT> inputs)
    {
        fixed (INPUT* p = inputs)
            _ = SendInput((uint)inputs.Length, p, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MouseInput(uint flags)
    {
        var i = new INPUT { type = INPUT_MOUSE };
        i.U.mi.dwFlags = flags;
        return i;
    }

    private static INPUT KeyInput(ushort vk, bool keyUp)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.U.ki.wVk = vk;
        // Arrow/navigation keys are "extended" keys; without KEYEVENTF_EXTENDEDKEY the game can
        // misread them (e.g. as numpad) or ignore them — which broke synthetic Ctrl+Arrow.
        var flags = keyUp ? KEYEVENTF_KEYUP : 0u;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        i.U.ki.dwFlags = flags;
        return i;
    }

    // Keys that require the extended-key flag when synthesized: the arrow keys, the navigation
    // cluster (Insert, Delete, Home, End, Page Up, Page Down), and the right-hand Ctrl and Alt.
    private static bool IsExtendedKey(int vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0xA3 or 0xA5;

    private static INPUT ScanCodeInput(ushort scanCode, bool keyUp)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.U.ki.wScan = scanCode;
        i.U.ki.dwFlags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0u);
        return i;
    }

    // ── P/Invoke ───────────────────────────────────────────────────────────

    private const uint INPUT_MOUSE         = 0;
    private const uint INPUT_KEYBOARD      = 1;
    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    private const uint MOUSEEVENTF_XDOWN      = 0x0080;
    private const uint MOUSEEVENTF_XUP        = 0x0100;
    private const uint XBUTTON1               = 0x0001;
    private const uint XBUTTON2               = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY  = 0x0001;
    private const uint KEYEVENTF_KEYUP        = 0x0002;
    private const uint KEYEVENTF_SCANCODE     = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT    mi;
        [FieldOffset(0)] public KEYBDINPUT    ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern unsafe uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
}
