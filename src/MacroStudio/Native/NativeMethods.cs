using System.Runtime.InteropServices;

namespace MacroStudio.Native;

internal static class NativeMethods
{
    public delegate nint HookProc(int nCode, nint wParam, nint lParam);
    public delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);

    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const uint WM_QUIT = 0x12, WM_HOTKEY = 0x312;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nint extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint extra; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUT_UNION u; }

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_SCANCODE = 8;
    public const uint MOUSEEVENTF_MOVE = 1, LEFTDOWN = 2, LEFTUP = 4, RIGHTDOWN = 8, RIGHTUP = 0x10,
        MIDDLEDOWN = 0x20, MIDDLEUP = 0x40, XDOWN = 0x80, XUP = 0x100, WHEEL = 0x800, HWHEEL = 0x1000,
        VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;

    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int id, HookProc fn, nint hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc cb, nint data);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint winIni);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);

    [StructLayout(LayoutKind.Sequential)] struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(nint process, int cls, ref PowerThrottling info, int size);

    /// <summary>
    /// Opt this process out of Windows power throttling (EcoQoS and timer coalescing). A minimized window puts a process in "eco" mode, which stretches timers to
    /// 100+ ms: key presses and clicks then arrive late. The app minimizes itself while a macro plays, so playback and recording both call this.
    /// (The call used to take a ValueTuple, which cannot be marshalled: it threw, the exception was swallowed, and the opt-out never happened.)
    /// </summary>
    public static bool SetPowerThrottling(bool allowed)
    {
        var st = new PowerThrottling { Version = 1, ControlMask = 5, StateMask = allowed ? 5u : 0u };   // 1 = execution speed, 4 = ignore timer resolution
        try { return SetProcessInformation(System.Diagnostics.Process.GetCurrentProcess().Handle, 4, ref st, 12); } catch { return false; }
    }
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
}
