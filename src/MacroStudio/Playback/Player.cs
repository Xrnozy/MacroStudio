using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Native;

namespace MacroStudio.Playback;

public interface IInputSink { void Send(in MacroEvent e); }

public interface IClock
{
    double NowMs { get; }
    void Sleep(double ms);
}

public sealed class RealClock : IClock
{
    public double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern nint CreateWaitableTimerExW(nint attrs, nint name, uint flags, uint access);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetWaitableTimer(nint t, ref long due, int period, nint cb, nint arg, bool resume);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint h, uint ms);

    [ThreadStatic] static nint _timer;        // one high-resolution timer per playback thread
    [ThreadStatic] static bool _noTimer;

    /// <summary>The last stretch before an event that is spun instead of slept: the timer may wake up to about this much late.</summary>
    internal const double SpinMs = 0.6;

    // Sleep on a high-resolution timer (sub-millisecond wake-up, no CPU) until SpinMs before due, spin only that last bit. Slices are capped so Stop reacts within 15 ms.
    public void Sleep(double ms)
    {
        if (ms > SpinMs + 0.2)
        {
            if (_timer == 0 && !_noTimer) { _timer = CreateWaitableTimerExW(0, 0, 0x2 /*HIGH_RESOLUTION*/, 0x100002 /*TIMER_MODIFY_STATE | SYNCHRONIZE*/); _noTimer = _timer == 0; }
            if (_timer != 0)
            {
                long due = -(long)(Math.Min(ms - SpinMs, 15) * 10_000);
                if (SetWaitableTimer(_timer, ref due, 0, 0, 0, false)) { WaitForSingleObject(_timer, 50); return; }
            }
            Thread.Sleep(1);   // no high-resolution timer (older Windows): the 1 ms tick
            return;
        }
        Thread.SpinWait(20);
    }
}

/// <summary>
/// Replays an event array on an absolute schedule (no drift). Tracks held keys/buttons so stop,
/// error and exit always release them.
/// </summary>
public sealed class Player
{
    readonly IInputSink _sink;
    readonly IClock _clock;
    readonly Dictionary<int, MacroEvent> _heldKeys = new();
    readonly Dictionary<MouseBtn, MacroEvent> _heldBtns = new();
    Thread? _thread;
    volatile bool _stop, _paused, _stepMode, _resync;
    int _stepTokens;
    volatile int _current;
    double _speed = 1;

    public Player(IInputSink sink, IClock? clock = null) { _sink = sink; _clock = clock ?? new RealClock(); }

    public int CurrentIndex => _current;
    public bool IsRunning => _thread is { IsAlive: true };
    public bool IsPaused => _paused;
    public bool StopRequested => _stop || ExternalStop?.Invoke() == true;
    public double MaxLatenessMs { get; private set; }
    public Exception? Error { get; private set; }

    /// <summary>Optional: pause before executing the event at this index (breakpoints). Called on the playback thread.</summary>
    public Func<int, bool>? BreakAt { get; set; }
    /// <summary>Optional: runs the When blocks (conditions, connected macros, restart).</summary>
    public IFlowHost? Flow { get => _flow; set { _flow = value; (value as FlowRunner)?.AttachRoot(this); } }
    IFlowHost? _flow;
    /// <summary>Optional: a nested player waits while this returns true (the main player's <see cref="PausedForNested"/>).</summary>
    public Func<bool>? ExternalPause { get; set; }
    volatile bool _whenStepped;
    /// <summary>
    /// The user paused (Pause() / breakpoint / step mode) and connected macros must wait too. False while a stepped When block runs: Step treats a When block
    /// as ONE step, the connected macro runs to completion during it. Pause() during that run makes it true again.
    /// </summary>
    public bool PausedForNested => _paused && !_whenStepped;
    /// <summary>Optional: a nested player stops when its parent does.</summary>
    public Func<bool>? ExternalStop { get; set; }
    /// <summary>Optional: where failures and breakpoint stops are recorded.</summary>
    public ExecutionLog? Log { get; set; }

    /// <summary>Raised on the playback thread once everything is released.</summary>
    public event Action? Finished;

    public double Speed
    {
        get => Volatile.Read(ref _speed);                  // a double written on the UI thread and read in the wait loop: keep the access atomic
        set { Volatile.Write(ref _speed, Math.Clamp(value, 0.05, 50)); _resync = true; }
    }

    public void Pause() { _whenStepped = false; _paused = true; }
    public void Resume() { _stepMode = false; _paused = false; Interlocked.Exchange(ref _stepTokens, 0); }   // a token left unconsumed must not fire an extra event at the next Step
    public void Stop() => _stop = true;

    /// <summary>Fire exactly one event, entering step mode if needed.</summary>
    public void Step()
    {
        _stepMode = true; _paused = true;
        Interlocked.Increment(ref _stepTokens);
    }

    public void Start(MacroEvent[] events, int from, int to, int loops, bool stepMode = false)
    {
        if (IsRunning) throw new InvalidOperationException("Already playing");
        Reset(stepMode); // before the thread exists: a Stop() or Step() right after Start() must not be wiped by the thread starting up
        _thread = new Thread(() =>
        {
            NativeMethods.timeBeginPeriod(1);
            NativeMethods.SetPowerThrottling(false);   // the app is minimized while a macro plays: without this Windows stretches timers and key presses arrive late
            try { RunCore(events, from, to, loops); }
            finally { NativeMethods.timeEndPeriod(1); }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Playback" };
        _thread.Start();
    }

    void Reset(bool stepMode)
    {
        _stop = false; _paused = stepMode; _stepMode = stepMode; _resync = true;
        Interlocked.Exchange(ref _stepTokens, 0);
        MaxLatenessMs = 0; Error = null;
    }

    /// <summary>Blocks until done. loops: 0 = forever. to is exclusive.</summary>
    public void Run(MacroEvent[] ev, int from, int to, int loops, bool stepMode = false)
    {
        if (IsRunning) throw new InvalidOperationException("Already playing");
        Reset(stepMode);
        RunCore(ev, from, to, loops);
    }

    void RunCore(MacroEvent[] ev, int from, int to, int loops)
    {
        to = Math.Min(to, ev.Length);
        try
        {
            if (from >= to) return;
            double baseT = ev[from].T, t0 = 0;
            for (int iter = 0; (loops <= 0 || iter < loops) && !_stop; iter++)
            {
                _resync = true;
                Flow?.LoopStarted();
                int i = from;
                while (i < to && !_stop)
                {
                    _current = i;
                    if (ExternalStop?.Invoke() == true) { _stop = true; break; }
                    if (BreakAt?.Invoke(i) == true) { _paused = true; Log?.Add(i, "debugger", LogResult.Warn, "paused at breakpoint"); }
                    var next = FlowStep.Continue;
                    while (!_stop)
                    {
                        double now = _clock.NowMs;
                        if (_paused || ExternalPause?.Invoke() == true)
                        {
                            if (_stepMode && Volatile.Read(ref _stepTokens) > 0 && Interlocked.Decrement(ref _stepTokens) >= 0) break;
                            _resync = true;       // time spent paused must not be "caught up" afterwards
                            _clock.Sleep(10);
                            continue;
                        }
                        double sp = Volatile.Read(ref _speed);
                        if (_resync) { t0 = now - (ev[i].T - baseT) / sp; _resync = false; }
                        double due = t0 + (ev[i].T - baseT) / sp;
                        if (now >= due) { MaxLatenessMs = Math.Max(MaxLatenessMs, now - due); break; }
                        if (ExternalStop?.Invoke() == true) { _stop = true; break; }
                        if (Flow != null)
                        {
                            double before = _clock.NowMs;
                            next = Flow.Poll();                                  // a watching When block may fire while waiting for the next step
                            if (next != FlowStep.Continue) break;
                            double spent = _clock.NowMs - before;
                            if (spent > 50) t0 += spent;                         // it ran a macro: that time is not part of this step's wait
                        }
                        _clock.Sleep(Flow != null ? Math.Min(due - now, 25) : due - now);   // short slices so a watching block can look in
                    }
                    if (_stop) break;
                    var e = ev[i];
                    if (next == FlowStep.Continue)
                    {
                        try
                        {
                            if (e.Kind == EventKind.When)
                            {
                                if (Flow != null) { _whenStepped = _stepMode; try { next = Flow.OnWhen(in e, i); } finally { _whenStepped = false; } }
                                else Log?.Add(i, "when", LogResult.Skipped, "conditions are not available in this run");
                            }
                            else { _sink.Send(in e); Track(e); }
                        }
                        catch (FlowRestartException) { next = FlowStep.Restart; }   // a watching block fired inside a waiting step
                        catch (FlowStopException) { next = FlowStep.Stop; }
                    }
                    if (next == FlowStep.Stop) { _stop = true; break; }
                    if (next == FlowStep.Restart) { ReleaseAll(); Flow?.Restarting(); i = from; _resync = true; continue; }
                    if ((e.IsTarget() || e.Kind == EventKind.When) && i + 1 < to)
                    {
                        // The step took longer than its recorded slot (it waited for the screen, glided, settled). Keep the recorded timeline: the next events stay on their
                        // original times when the overrun fits inside the recorded gap, and only the shortfall is shifted, so nothing bursts out and nothing is skipped.
                        double late = _clock.NowMs - (t0 + (ev[i + 1].T - baseT) / Volatile.Read(ref _speed));
                        if (late > 0) t0 += late;
                    }
                    i++;
                }
            }
        }
        catch (Exception ex) { Error = ex; Log?.Add(_current, "playback", LogResult.Failed, ex.Message); }
        finally
        {
            ReleaseAll();
            try { Flow?.Ended(); } catch { }
            try { Finished?.Invoke(); } catch { }          // a subscriber must not take the playback thread (or a synchronous Run) down with it
        }
    }

    void Track(in MacroEvent e)
    {
        switch (e.Kind)
        {
            case EventKind.KeyDown: _heldKeys[e.Vk] = e; break;
            case EventKind.KeyUp: _heldKeys.Remove(e.Vk); break;
            case EventKind.ButtonDown: _heldBtns[e.Button] = e; break;
            case EventKind.ButtonUp: _heldBtns.Remove(e.Button); break;
        }
    }

    void ReleaseAll()
    {
        foreach (var d in _heldKeys.Values.ToArray())
            try { var up = d with { Kind = EventKind.KeyUp }; _sink.Send(in up); } catch { }
        foreach (var d in _heldBtns.Values.ToArray())
            try { var up = d with { Kind = EventKind.ButtonUp }; _sink.Send(in up); } catch { }
        _heldKeys.Clear();
        _heldBtns.Clear();
    }
}
