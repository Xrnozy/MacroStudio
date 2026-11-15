using System.Runtime.InteropServices;

namespace MacroStudio.Native;

/// <summary>
/// Raw mouse deltas (WM_INPUT), the way games read the mouse. While a game hides and locks the cursor (Roblox camera on right-drag,
/// shift-lock, first person) the cursor position never changes, so position hooks see nothing; the raw deltas still do.
/// A message-only window on its own thread receives them even when another program has the focus.
/// </summary>
public sealed class RawMouse : IDisposable
{
    delegate nint WndProc(nint h, uint msg, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public nint proc; public int cbCls, cbWnd; public nint inst, icon, cursor, bg; public string? menu, cls; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort page, usage; public uint flags; public nint target; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint type, size; public nint device, wParam; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint h; public uint msg; public nint w, l; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int size, flags; public nint cursor; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(int ex, string cls, string? name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(nint raw, uint cmd, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, nint h, uint min, uint max);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostMessageW(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);

    /// <summary>The pointer is hidden: a program is steering with the mouse (camera look) and the cursor position means nothing.</summary>
    public static bool CursorHidden()
    {
        var ci = new CURSORINFO { size = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref ci) && (ci.flags & 1) == 0;   // CURSOR_SHOWING
    }

    readonly object _l = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, RawMouse> Owners = new();
    // One window class for the whole process (it can only be registered once), one static delegate that lives forever, routed to the instance that owns the window.
    static readonly WndProc SProc = (h, m, w, l) =>
    {
        Owners.TryGetValue(h, out RawMouse? o);
        return o is not null ? o.Proc(h, m, w, l) : DefWindowProcW(h, m, w, l);
    };
    static bool _registered;
    static readonly object Reg = new();
    Thread? _thread;
    nint _hwnd;
    long _dx, _dy;
    int _lastAbsX = int.MinValue, _lastAbsY;

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                var inst = GetModuleHandleW(null);
                lock (Reg)
                    if (!_registered)
                    {
                        var wc = new WNDCLASS { proc = Marshal.GetFunctionPointerForDelegate(SProc), inst = inst, cls = "MacroStudioRawMouse" };
                        _registered = RegisterClassW(ref wc) != 0;
                    }
                _hwnd = CreateWindowExW(0, "MacroStudioRawMouse", null, 0, 0, 0, 0, 0, -3 /*HWND_MESSAGE*/, 0, inst, 0);
                if (_hwnd != 0) Owners[_hwnd] = this;
                if (_hwnd != 0)
                    RegisterRawInputDevices([new RAWINPUTDEVICE { page = 1, usage = 2, flags = 0x100 /*RIDEV_INPUTSINK*/, target = _hwnd }], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch { }
            ready.Set();
            if (_hwnd == 0) return;
            while (GetMessageW(out var m, 0, 0, 0) > 0) DispatchMessageW(ref m);
        }) { IsBackground = true, Name = "RawMouse" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(2000);
    }

    /// <summary>Movement received since the last call (screen pixels' worth of device counts), or null when there was none.</summary>
    public (int Dx, int Dy)? Take()
    {
        lock (_l)
        {
            if (_dx == 0 && _dy == 0) return null;
            var r = ((int)_dx, (int)_dy); _dx = _dy = 0; return r;
        }
    }

    nint Proc(nint h, uint msg, nint w, nint l)
    {
        if (msg == 0x00FF)   // WM_INPUT
        {
            try { Read(l); } catch { }
        }
        return DefWindowProcW(h, msg, w, l);
    }

    void Read(nint raw)
    {
        uint size = 0, head = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(raw, 0x10000003 /*RID_INPUT*/, 0, ref size, head);
        if (size == 0 || size > 512) return;
        nint buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(raw, 0x10000003, buf, ref size, head) != size) return;
            if (Marshal.ReadInt32(buf) != 0) return;                        // RIM_TYPEMOUSE
            int o = (int)head;
            ushort flags = (ushort)Marshal.ReadInt16(buf, o);
            int x = Marshal.ReadInt32(buf, o + 12), y = Marshal.ReadInt32(buf, o + 16);
            int dx = x, dy = y;
            if ((flags & 1) != 0)                                           // absolute (a remote desktop client): 0..65535 over the screen, so take the change
            {
                int px = (int)((long)x * GetSystemMetrics(0) / 65535), py = (int)((long)y * GetSystemMetrics(1) / 65535);
                dx = _lastAbsX == int.MinValue ? 0 : px - _lastAbsX; dy = _lastAbsX == int.MinValue ? 0 : py - _lastAbsY;
                _lastAbsX = px; _lastAbsY = py;
            }
            if (dx == 0 && dy == 0) return;
            if (!CursorHidden()) return;                                    // a visible cursor is already recorded by position
            lock (_l) { _dx += dx; _dy += dy; }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_hwnd != 0) { Owners.TryRemove(_hwnd, out _); PostMessageW(_hwnd, 0x0012 /*WM_QUIT*/, 0, 0); _thread?.Join(500); }
        _hwnd = 0; _thread = null;
    }
}
