using System.ComponentModel;
using System.Diagnostics;
using MacroStudio.Model;
using static MacroStudio.Native.NativeMethods;

namespace MacroStudio.Native;

/// <summary>A captured input with a QPC timestamp (Stopwatch ticks).</summary>
public readonly record struct RawInput(long Ticks, EventKind Kind, int Vk, int Scan, bool Ext, int X, int Y, MouseBtn Button, int Delta);

/// <summary>Global low-level keyboard + mouse hooks on a dedicated message-loop thread. The sink must return fast (hook timeout).</summary>
public sealed unsafe class InputHooks : IDisposable
{
    readonly Action<RawInput> _sink;
    readonly HookProc _kbProc, _msProc; // fields keep the delegates alive
    Thread? _thread;
    uint _threadId;
    nint _kbHook, _msHook;
    readonly bool[] _seen = new bool[256];            // key / button state the hooks have reported
    int _seenX = int.MinValue, _seenY;
    volatile bool _stop;
    Thread? _poll;
    RawMouse? _raw;

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetProcessInformation(nint process, int cls, ref (uint Version, uint Control, uint State) info, int size);

    /// <summary>A minimized window makes Windows put the process in "eco" mode: timers stretch to 100+ ms and short key presses slip between polls. Opt out while recording.</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern nint CreateWaitableTimerExW(nint attrs, nint name, uint flags, uint access);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetWaitableTimer(nint t, ref long due, int period, nint cb, nint arg, bool resume);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint h, uint ms);
    readonly long[] _hookAt = new long[256];          // when a hook last reported each key / button

    static void NoPowerThrottling()
    {
        var st = (1u, 5u, 0u);   // version 1; we control EXECUTION_SPEED (1) and IGNORE_TIMER_RESOLUTION (4); both off, so a minimized window keeps 1 ms timers
        try { SetProcessInformation(Process.GetCurrentProcess().Handle, 4, ref st, 12); } catch { }
    }

    /// <summary>Our own SendInput output is flagged injected; skipping it keeps playback out of recordings.</summary>
    public bool IgnoreInjected { get; set; } = true;

    /// <summary>Also compare against the real key / button / cursor state (see <see cref="Poll"/>). For recording; the playback guard must not see its own output.</summary>
    public bool PollFallback { get; set; }

    /// <summary>Diagnostics of this recording (counts of what each capture path saw, slow callbacks).</summary>
    public MacroStudio.Recording.RecordLog? Log { get; set; }

    public InputHooks(Action<RawInput> sink)
    {
        _sink = sink;
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
    }

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        Exception? error = null;
        _thread = new Thread(() =>
        {
            try
            {
                _threadId = GetCurrentThreadId();
                var module = GetModuleHandle(null);
                _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, module, 0);
                if (_kbHook == 0) throw new Win32Exception();
                _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, module, 0);
                if (_msHook == 0) throw new Win32Exception();
            }
            catch (Exception e) { error = e; }
            ready.Set();
            if (error == null) while (GetMessage(out _, 0, 0, 0) > 0) { }
            if (_kbHook != 0) UnhookWindowsHookEx(_kbHook);
            if (_msHook != 0) UnhookWindowsHookEx(_msHook);
            _kbHook = _msHook = 0;
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "InputHooks" };
        _thread.Start();
        ready.Wait();
        if (error != null) { _thread.Join(); _thread = null; throw error; }
        _stop = false;
        if (PollFallback) { _raw = new RawMouse(); _raw.Start(); SetPowerThrottling(false); timeBeginPeriod(1); _poll = new Thread(Poll) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "InputPoll" }; _poll.Start(); }
    }

    public void Dispose()
    {
        if (_thread == null) return;
        if (_poll != null) { _stop = true; _poll.Join(500); _poll = null; timeEndPeriod(1); _raw?.Dispose(); _raw = null; }
        PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread.Join(2000);
        _thread = null;
    }

    /// <summary>
    /// Safety net: some programs (anti-cheat games, remote sessions) make the hooks skip or lose input. Compare what the hooks reported with the real
    /// key / button / cursor state, and report a difference that lasts two polls (so a hook still in flight is never doubled).
    /// </summary>
    void Poll()
    {
        var bad = new int[256]; int badMove = 0;
        nint timer = CreateWaitableTimerExW(0, 0, 0x2 /*HIGH_RESOLUTION*/, 0x1F0003);   // exact 4 ms even in a minimized window
        long grace = Stopwatch.Frequency / 50;      // a hook report this recent is still in flight: not a difference
        (_seenX, _seenY) = Screens.CursorPos();   // the hooks may never report a move (that is the case this exists for)
        long lastLoop = Stopwatch.GetTimestamp(); int tick = 0;
        while (!_stop)
        {
            if (timer != 0) { long due = -40000; SetWaitableTimer(timer, ref due, 0, 0, 0, false); WaitForSingleObject(timer, 50); } else Thread.Sleep(4);
            { long nowL = Stopwatch.GetTimestamp(); double gap = (nowL - lastLoop) * 1000.0 / Stopwatch.Frequency; lastLoop = nowL;
              Log?.Max("backup check interval (ms, wanted 4)", gap); if (gap > 30) Log?.Event("slow: backup check ran > 30 ms apart", $"{gap:F0} ms"); }
            ScanKeys(tick++, vk => (GetAsyncKeyState(vk) & 0x8000) != 0, grace);
            if (_raw?.Take() is { } rel)
            {
                Log?.Count("raw: look movement events"); Log?.Max("raw: largest look step (counts)", Math.Max(Math.Abs(rel.Dx), Math.Abs(rel.Dy)));
                try { _sink(new RawInput(Stopwatch.GetTimestamp(), EventKind.RelMove, 0, 0, false, rel.Dx, rel.Dy, default, 0)); } catch { }
            }
            var (cx, cy) = Screens.CursorPos();
            if (RawMouse.CursorHidden()) { _seenX = cx; _seenY = cy; badMove = 0; continue; }
            if (_seenX != int.MinValue && (cx != _seenX || cy != _seenY))
            {
                if (++badMove >= 2) { badMove = 0; _seenX = cx; _seenY = cy; Log?.Count("poller: caught cursor move the hooks never reported"); try { _sink(new RawInput(Stopwatch.GetTimestamp(), EventKind.MouseMove, 0, 0, false, cx, cy, default, 0)); } catch { } }
            }
            else badMove = 0;
        }
        if (_raw?.Take() is { } last)   // movement that arrived in the final milliseconds
            try { _sink(new RawInput(Stopwatch.GetTimestamp(), EventKind.RelMove, 0, 0, false, last.Dx, last.Dy, default, 0)); } catch { }
    }

    /// <summary>One backup pass over the keys that are due this tick (see <see cref="KeyPollPlan"/>); the key-state reader is a seam for tests.</summary>
    internal void ScanKeys(int tick, Func<int, bool> isDown, long grace)
    {
        for (int vk = 1; vk < 255; vk++)
        {
            if (vk is 0x03 or 0x07 or 0x0A or 0x0B or 0x10 or 0x11 or 0x12 or 0xE5) continue;   // generic Shift/Ctrl/Alt: the left/right ones are reported
            if (!KeyPollPlan.Due(vk, tick, _seen[vk])) continue;
            bool down = isDown(vk);
            if (down == _seen[vk]) continue;
            long t = Stopwatch.GetTimestamp();
            if (t - Volatile.Read(ref _hookAt[vk]) < grace) continue;
            _seen[vk] = down;
            Log?.Event($"poller: caught {(vk is 1 or 2 or 4 or 5 or 6 ? "button" : "key")} {(down ? "down" : "up")} the hooks never reported", $"vk=0x{vk:X} (hook last heard this key {(Volatile.Read(ref _hookAt[vk]) == 0 ? "never" : $"{(t - _hookAt[vk]) * 1000.0 / Stopwatch.Frequency:F0} ms ago")})");
            try
            {
                if (vk is 1 or 2 or 4 or 5 or 6)
                {
                    var (x, y) = Screens.CursorPos();
                    var b = vk switch { 1 => MouseBtn.Left, 2 => MouseBtn.Right, 4 => MouseBtn.Middle, 5 => MouseBtn.X1, _ => MouseBtn.X2 };
                    _sink(new RawInput(t, down ? EventKind.ButtonDown : EventKind.ButtonUp, 0, 0, false, x, y, b, 0));
                }
                else
                {
                    bool ext = vk is >= 0x21 and <= 0x2E or 0xA3 or 0xA5 or 0x6F or 0x90;
                    _sink(new RawInput(t, down ? EventKind.KeyDown : EventKind.KeyUp, vk, (int)MapVirtualKey((uint)vk, 0), ext, 0, 0, default, 0));
                }
            }
            catch { }
        }
    }

    /// <summary>Windows drops a hook that answers too slowly: log how long ours takes.</summary>
    void Took(long t0, string which)
    {
        if (Log == null) return;
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        Log.Max(which == "mouse" ? "mouse hook callback took (ms)" : "keyboard hook callback took (ms)", ms);
        if (ms > 5) Log.Event($"slow: {which} hook callback > 5 ms", $"{ms:F1} ms");
    }

    nint KeyboardProc(int n, nint w, nint l)
    {
        if (n >= 0)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                var k = (KBDLLHOOKSTRUCT*)l;
                bool injected = (k->flags & 0x12) != 0; // LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED
                if (IgnoreInjected && injected) Log?.Count("hook: skipped injected key (sent by software, or by a remote desktop)");
                if (!(IgnoreInjected && injected))
                {
                    int msg = (int)w;
                    bool down = msg is 0x100 or 0x104, up = msg is 0x101 or 0x105;
                    if (down || up)
                    {
                        Log?.Count(down ? "hook: key down" : "hook: key up");
                        _seen[(int)k->vkCode & 0xFF] = down; _hookAt[(int)k->vkCode & 0xFF] = Stopwatch.GetTimestamp();
                        _sink(new RawInput(Stopwatch.GetTimestamp(), down ? EventKind.KeyDown : EventKind.KeyUp,
                            (int)k->vkCode, (int)k->scanCode, (k->flags & 1) != 0, 0, 0, default, 0));
                    }
                }
            }
            catch (Exception ex) { Log?.Event("ERROR in keyboard hook", ex.Message); /* never throw into the OS hook chain */ }
            Took(t0, "keyboard");
        }
        return CallNextHookEx(0, n, w, l);
    }

    nint MouseProc(int n, nint w, nint l)
    {
        if (n >= 0)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                var m = (MSLLHOOKSTRUCT*)l;
                if (IgnoreInjected && (m->flags & 1) != 0) Log?.Count("hook: skipped injected mouse input (sent by software, or by a remote desktop)");
                if (!(IgnoreInjected && (m->flags & 1) != 0)) // LLMHF_INJECTED
                {
                    int hi = (short)(m->mouseData >> 16);
                    EventKind kind; MouseBtn btn = MouseBtn.Left; int delta = 0; bool ok = true;
                    switch ((int)w)
                    {
                        case 0x200: kind = EventKind.MouseMove; break;
                        case 0x201: kind = EventKind.ButtonDown; break;
                        case 0x202: kind = EventKind.ButtonUp; break;
                        case 0x204: kind = EventKind.ButtonDown; btn = MouseBtn.Right; break;
                        case 0x205: kind = EventKind.ButtonUp; btn = MouseBtn.Right; break;
                        case 0x207: kind = EventKind.ButtonDown; btn = MouseBtn.Middle; break;
                        case 0x208: kind = EventKind.ButtonUp; btn = MouseBtn.Middle; break;
                        case 0x20B: kind = EventKind.ButtonDown; btn = hi == 2 ? MouseBtn.X2 : MouseBtn.X1; break;
                        case 0x20C: kind = EventKind.ButtonUp; btn = hi == 2 ? MouseBtn.X2 : MouseBtn.X1; break;
                        case 0x20A: kind = EventKind.Wheel; delta = hi; break;
                        case 0x20E: kind = EventKind.HWheel; delta = hi; break;
                        default: kind = default; ok = false; break;
                    }
                    _seenX = m->pt.X; _seenY = m->pt.Y;
                    if (ok && kind is EventKind.ButtonDown or EventKind.ButtonUp)
                    {
                        int bi = btn switch { MouseBtn.Left => 1, MouseBtn.Right => 2, MouseBtn.Middle => 4, MouseBtn.X1 => 5, _ => 6 };
                        _seen[bi] = kind == EventKind.ButtonDown; _hookAt[bi] = Stopwatch.GetTimestamp();
                    }
                    if (ok && kind == EventKind.MouseMove && PollFallback && RawMouse.CursorHidden()) { Log?.Count("hook: move skipped, cursor hidden (recorded as raw look movement instead)"); ok = false; }
                    if (ok) Log?.Count(MacroStudio.Recording.RecordLog.HookMouseKey[(int)kind]);
                    if (ok) _sink(new RawInput(Stopwatch.GetTimestamp(), kind, 0, 0, false, m->pt.X, m->pt.Y, btn, delta));
                }
            }
            catch (Exception ex) { Log?.Event("ERROR in mouse hook", ex.Message); }
            Took(t0, "mouse");
        }
        return CallNextHookEx(0, n, w, l);
    }
}

/// <summary>
/// Which keys the backup poller reads on which tick. Mouse buttons and keys the hooks reported as down are read every tick (a release must not wait);
/// every other key is read every <see cref="Stride"/> ticks, staggered so each tick does the same share: 8 ms for any key to be caught, half the system calls.
/// </summary>
internal static class KeyPollPlan
{
    public const int Stride = 2;
    public static bool Due(int vk, int tick, bool seenDown) => seenDown || vk is 1 or 2 or 4 or 5 or 6 || (vk + tick) % Stride == 0;
}
