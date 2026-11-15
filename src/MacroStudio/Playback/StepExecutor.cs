using MacroStudio.Model;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Playback;

/// <summary>One fresh observation of a target, in virtual-desktop pixels.</summary>
/// <param name="Seq">Different value = a separate observation (see <see cref="WatchState.Seq"/>).</param>
public readonly record struct Sighting(Detection Hit, long Seq, DateTime SeenUtc, bool Ambiguous = false)
{
    public Rect Rect => Hit.Rect;
    public Point Center => Hit.Center;
    public double Score => Hit.Score;
}

/// <summary>Finds registered targets on the live screen.</summary>
public interface ITargetFinder
{
    /// <summary>Display name, or null when the target is not in the library.</summary>
    string? NameOf(string id);
    /// <summary>The target's own acceptance threshold (used to warn when a click is made on a thin margin).</summary>
    double ThresholdOf(string id);
    /// <summary>The current sighting, or null when the target is not visible right now.</summary>
    Sighting? Find(string id);
    /// <summary>Where the step about to click this target was recorded: the nearest of several look-alikes is the one meant.</summary>
    void Expect(string id, int x, int y) { }
}

public sealed class TargetNotFoundException(string message) : Exception(message);
public sealed class StepFailedException(string message) : Exception(message);

/// <summary>
/// Executes the adaptive steps (TargetClick, WaitTarget, WaitGone); every other event passes straight through.
/// A click is: wait for the target, require a stable high-confidence sighting, re-check it at the last moment, click at the
/// anchor point relative to where the target is NOW, optionally verify the effect, and on failure retry, recover, fall back
/// or stop as configured. Press and release are always paired, even if stop is requested mid-click.
/// </summary>
public sealed class StepExecutor(IInputSink inner, ITargetFinder finder, IClock clock, Func<bool> stopRequested,
    ExecutionLog? log = null, Func<int>? currentIndex = null, Func<bool>? fallbackSafe = null,
    Func<double>? speed = null, Func<bool>? dynamicPath = null, Func<(int X, int Y)>? readCursor = null, bool correctCursor = false) : IInputSink, IClickReporter
{
    /// <summary>Where the last adaptive click really landed (virtual-desktop pixels), or null before the first one.</summary>
    public (int X, int Y)? LastClick { get; private set; }

    /// <summary>Called on every poll while a step waits for the screen, so a watching When block can act mid-wait (it may throw <see cref="FlowRestartException"/>).</summary>
    public Action? Idle { get; set; }

    const int PollMs = 40, HoldMs = 60, DefaultTimeoutMs = 10_000, GoneDebounceMs = 200, StableRadius = 3;
    const double LowMargin = 0.03;

    readonly Dictionary<string, (double At, Point P)> _lastClick = new();
    (int X, int Y)? _cursor;                      // where this executor last put the cursor (or saw it go)

    public void Send(in MacroEvent e)
    {
        switch (e.Kind)
        {
            case EventKind.TargetClick: Click(e); break;
            case EventKind.WaitTarget: Wait(e, visible: true); break;
            case EventKind.WaitGone: Wait(e, visible: false); break;
            default:
                if (e.HasPos()) _cursor = (e.X, e.Y);
                inner.Send(in e);
                break;
        }
    }

    // ---------- click ----------

    void Click(MacroEvent e)
    {
        var o = e.Step ?? StepOptions.Legacy(e.Extended); // steps made before adaptive options keep their old behaviour
        string id = e.Target ?? "";
        string name = finder.NameOf(id) ?? throw new TargetNotFoundException($"Target '{id}' is not in the library (deleted or from another PC).");
        int timeout = e.Delta > 0 ? e.Delta : DefaultTimeoutMs;
        double t0 = clock.NowMs;
        string failure = "not found";
        if (o.FallbackX != 0 || o.FallbackY != 0) finder.Expect(id, o.FallbackX, o.FallbackY);

        for (int attempt = 0; attempt <= o.Retries && !stopRequested(); attempt++)
        {
            if (attempt > 0)
            {
                Log(name, LogResult.Retry, $"retry {attempt}/{o.Retries}: {failure}");
                Pause(o.RetryDelayMs);
            }

            var s = WaitVisible(id, o.MinConfidence, o.RequireStable, timeout);
            if (stopRequested()) return;
            if (s is not { } seen) { failure = o.MinConfidence > 0 ? $"not found with confidence >= {o.MinConfidence:0.00} within {timeout / 1000.0:0.#} s" : $"not found within {timeout / 1000.0:0.#} s"; continue; }

            // last-moment check: the sighting must still be there, near where we are about to click
            if (finder.Find(id) is not { } now || Dist(now.Center, seen.Center) > StableRadius + 6) { failure = "disappeared just before the click"; continue; }
            seen = now;

            var p = ClickPoint.Compute(seen.Hit, o, e.X, e.Y);
            if (ClickPoint.Raw(seen.Hit, o, e.X, e.Y) != p) Log(name, LogResult.Warn, $"the stored click point ({o.AnchorText}, offset {e.X},{e.Y}) lies outside the target: clicking the CENTRE of the target. Re-convert the click to the right target if this is not the spot you want", seen.Score, p.X, p.Y);
            if (RepeatBlocked(id, p, o.MinRepeatMs)) { Log(name, LogResult.Skipped, $"repeat click on the same spot within {o.MinRepeatMs} ms prevented", seen.Score, p.X, p.Y); return; }

            if (seen.Ambiguous) Log(name, LogResult.Warn, "several similar matches and nothing told them apart: the click may land on the wrong one (re-capture the target with its surroundings visible)", seen.Score, p.X, p.Y);
            double thr = finder.ThresholdOf(id);
            if (seen.Hit.Method != "template") Log(name, LogResult.Warn, $"clicking on a {(seen.Hit.Method == "shape" ? $"shape match ({seen.Hit.Score:0.00}): same strokes, different colours (greyed out or recoloured)" : seen.Hit.Method == "patches" ? $"partly hidden target ({seen.Hit.Visible:P0} visible)" : "feature match (rotated / resized / hidden)")}", seen.Score, p.X, p.Y);
            else if (seen.Score < thr + LowMargin) Log(name, LogResult.Warn, $"low confidence margin: {seen.Score:0.00} vs threshold {thr:0.00}", seen.Score, p.X, p.Y);

            // how the mouse gets there: instantly (exact recorded path, then a jump) or by a smooth movement that follows the target
            var mode = dynamicPath?.Invoke() == false ? MoveMode.Jump : o.Move;
            if (mode != MoveMode.Jump)
            {
                // The watcher reports where the target WAS a moment ago. Track its velocity and aim where it will be; a lone far-away "new position" is a look-alike.
                var trk = new Tracker();
                trk.Update(seen, p, clock.NowMs);
                Point Aim()
                {
                    if (finder.Find(id) is { } s2) trk.Update(s2, ClickPoint.Compute(s2.Hit, o, e.X, e.Y), clock.NowMs);
                    return trk.Predict(clock.NowMs);
                }
                Glide(p, Aim, o.GlideMs);
                if (stopRequested()) return; // stop pressed while travelling: never click after a stop
                if (Settle(Aim, trk, Math.Min(seen.Hit.Rect.Width, seen.Hit.Rect.Height)) is not { } landed) { failure = "could not catch the target: it kept moving"; continue; }
                p = landed;
            }
            if (stopRequested()) return;     // stop pressed while catching the target: never click after a stop
            Press(e.Button, p.X, p.Y);
            LastClick = (p.X, p.Y);
            _lastClick[id] = (clock.NowMs, p);

            if (!Verify(o, id, out var vfail)) { failure = vfail; Recover(o, name); continue; }
            if (!string.IsNullOrWhiteSpace(o.DiscordText))      // "click, then tell Discord": one step, always in that order
            {
                var note = new MacroEvent(0, EventKind.Discord, Extended: o.DiscordShot, Target: o.DiscordText);
                inner.Send(in note);
            }
            Log(name, LogResult.Ok, $"clicked {e.Button} at ({p.X}, {p.Y}) anchor {o.AnchorText}, target box {seen.Hit.Rect.X},{seen.Hit.Rect.Y} {seen.Hit.Rect.Width}x{seen.Hit.Rect.Height} ({seen.Hit.Method})" + (attempt > 0 ? $" after {attempt} retr{(attempt == 1 ? "y" : "ies")}" : ""), seen.Score, p.X, p.Y, clock.NowMs - t0);
            return;
        }
        if (stopRequested()) return;

        // every attempt failed
        switch (o.OnFail)
        {
            case FailAction.Skip:
                Log(name, LogResult.Skipped, $"skipped: {failure}", null, null, null, clock.NowMs - t0);
                return;
            case FailAction.UseRecordedPosition when fallbackSafe?.Invoke() == true:
                Log(name, LogResult.Fallback, $"{failure}; using the recorded position ({o.FallbackX}, {o.FallbackY})", null, o.FallbackX, o.FallbackY, clock.NowMs - t0);
                Press(e.Button, o.FallbackX, o.FallbackY);
                return;
            case FailAction.UseRecordedPosition:
                Log(name, LogResult.Failed, $"{failure}; the recorded position is not used because the screen layout changed since recording", null, null, null, clock.NowMs - t0);
                throw new StepFailedException($"Target '{name}': {failure}. Fallback to the recorded position refused: the screen layout changed since recording.");
        }
        Log(name, LogResult.Failed, failure, null, null, null, clock.NowMs - t0);
        throw new TargetNotFoundException($"Target '{name}' {failure}.");
    }

    /// <summary>After the click: did it do what was expected?</summary>
    bool Verify(StepOptions o, string id, out string failure)
    {
        failure = "";
        switch (o.Verify)
        {
            case VerifyMode.TargetGone when !WaitUntilGone(id, o.VerifyTimeoutMs):
                failure = "the target was still visible after the click";
                return false;
            case VerifyMode.OtherTargetAppears when o.VerifyTarget is { } other && WaitVisible(other, 0, false, o.VerifyTimeoutMs) is null:
                failure = $"'{finder.NameOf(other) ?? other}' did not appear after the click";
                return false;
        }
        return true;
    }

    void Recover(StepOptions o, string name)
    {
        if (o.RecoveryKey > 0)
        {
            Log(name, LogResult.Warn, $"recovery: pressing key {EventInfo.KeyName(o.RecoveryKey)}");
            var down = new MacroEvent(0, EventKind.KeyDown, o.RecoveryKey);
            inner.Send(in down);
            Pause(40, ignoreStop: true);
            var up = down with { Kind = EventKind.KeyUp };
            inner.Send(in up);
            Pause(200);
        }
        if (o.RecoveryTarget is { } rt && WaitVisible(rt, 0, false, 1500) is { } s)
        {
            var p = s.Center;
            Log(name, LogResult.Warn, $"recovery: clicking '{finder.NameOf(rt) ?? rt}'", s.Score, p.X, p.Y);
            Press(MouseBtn.Left, p.X, p.Y);
            Pause(200);
        }
    }

    // ---------- waits ----------

    void Wait(MacroEvent e, bool visible)
    {
        string id = e.Target ?? "";
        string name = finder.NameOf(id) ?? throw new TargetNotFoundException($"Target '{id}' is not in the library (deleted or from another PC).");
        var o = e.Step;
        int timeout = e.Delta > 0 ? e.Delta : DefaultTimeoutMs;
        double t0 = clock.NowMs;
        bool ok = visible ? WaitVisible(id, o?.MinConfidence ?? 0, o?.RequireStable ?? false, timeout) is not null : WaitUntilGone(id, timeout);
        if (stopRequested()) return;
        double ms = clock.NowMs - t0;
        if (ok) { Log(name, LogResult.Ok, visible ? "visible" : "gone", visible ? finder.Find(id)?.Score : null, null, null, ms); return; }
        string msg = visible ? $"did not appear within {timeout / 1000.0:0.#} s" : $"was still visible after {timeout / 1000.0:0.#} s";
        if (e.Extended || o?.OnFail == FailAction.Skip) { Log(name, LogResult.Skipped, msg + "; continuing", null, null, null, ms); return; }
        Log(name, LogResult.Failed, msg, null, null, null, ms);
        throw new TargetNotFoundException($"Target '{name}' {msg}.");
    }

    /// <summary>Wait for a sighting with at least <paramref name="minConf"/> confidence; optionally two separate observations must agree on the position.</summary>
    Sighting? WaitVisible(string id, double minConf, bool stable, int timeoutMs)
    {
        double deadline = clock.NowMs + timeoutMs;
        Sighting? prev = null;
        while (!stopRequested())
        {
            Idle?.Invoke();
            if (finder.Find(id) is { } s && (minConf <= 0 || s.Score >= minConf))
            {
                if (!stable) return s;
                if (prev is { } p && p.Seq != s.Seq && Dist(p.Center, s.Center) <= StableRadius) return s; // second, separate observation agrees
                if (prev is null || prev.Value.Seq != s.Seq) prev = prev is { } q && Dist(q.Center, s.Center) <= StableRadius ? q : s;
            }
            else prev = null; // lost or too weak: start the agreement over
            if (clock.NowMs >= deadline) return null;
            Pause(PollMs);
        }
        return null;
    }

    /// <summary>Gone = not seen for <see cref="GoneDebounceMs"/> in a row, so one missed frame does not count as disappearance.</summary>
    bool WaitUntilGone(string id, int timeoutMs)
    {
        double deadline = clock.NowMs + timeoutMs, goneSince = -1;
        while (!stopRequested())
        {
            Idle?.Invoke();
            if (finder.Find(id) is null)
            {
                if (goneSince < 0) goneSince = clock.NowMs;
                if (clock.NowMs - goneSince >= GoneDebounceMs) return true;
            }
            else goneSince = -1;
            if (clock.NowMs >= deadline) return false;
            Pause(PollMs);
        }
        return false;
    }

    // ---------- primitives ----------

    const double ReaimRadius = 160;

    /// <summary>Last observations of a target: its velocity and where it is about to be (the sightings lag the screen by tens of ms).</summary>
    sealed class Tracker
    {
        const double LatencyMs = 60, WindowMs = 160, MaxLeadMs = 250;
        readonly List<(double T, Point P)> _obs = new();
        long _seq = -1; Point? _pending;
        public long Seq => _seq;
        Point Last => _obs[^1].P;

        public void Update(Sighting s, Point p, double now)
        {
            if (s.Seq == _seq) return;
            _seq = s.Seq;
            if (_obs.Count > 0 && Dist(p, Last) > ReaimRadius)
            {
                if (_pending is not { } q || Dist(q, p) > 40) { _pending = p; return; }   // moved far: believe it only when a second look agrees
                _obs.Clear();
            }
            _pending = null;
            _obs.Add((now, p));
            while (_obs.Count > 1 && now - _obs[0].T > WindowMs) _obs.RemoveAt(0);
        }

        public (double X, double Y) Velocity(double now)
        {
            if (_obs.Count < 2 || now - _obs[^1].T > 250 || _obs[^1].P == _obs[^2].P) return (0, 0);   // the latest look shows it standing still
            var a = _obs[0]; var b = _obs[^1];
            double dt = b.T - a.T;
            if (dt < 30) return (0, 0);
            double vx = (b.P.X - a.P.X) / dt, vy = (b.P.Y - a.P.Y) / dt;
            return vx * vx + vy * vy < 0.03 * 0.03 ? (0, 0) : (vx, vy);
        }

        public bool Moving(double now) => Velocity(now) != (0, 0);

        public Point Predict(double now)
        {
            var (vx, vy) = Velocity(now);
            double lead = Math.Min(now - _obs[^1].T + LatencyMs, MaxLeadMs);
            return new Point((int)Math.Round(Last.X + vx * lead), (int)Math.Round(Last.Y + vy * lead));
        }
    }

    /// <summary>
    /// After the glide: keep steering the pointer onto the target's predicted click point until it is there and a fresh look agrees,
    /// so a target dragged around while the pointer travelled is still hit. Null when it could not be caught in time.
    /// </summary>
    Point? Settle(Func<Point> aim, Tracker trk, int size)
    {
        double deadline = clock.NowMs + 4000, arrived = -1; long seq = 0; Point t = aim();
        while (!stopRequested() && clock.NowMs < deadline)
        {
            t = aim();
            if ((_cursor ?? readCursor?.Invoke()) is not { } c) return t;
            double d = Dist(new Point(c.X, c.Y), t), tol = trk.Moving(clock.NowMs) ? Math.Max(3, 0.25 * size) : 1.5;
            if (d <= tol)
            {
                if (arrived < 0) { arrived = clock.NowMs; seq = trk.Seq; }
                if (!trk.Moving(clock.NowMs) || trk.Seq != seq || clock.NowMs - arrived >= 120) return t;   // at rest and on it: click now (the sighting was already confirmed); a moving target waits for a fresh look
                Pause(8);                                                      // on the spot: just wait for that look, do not move
                continue;
            }
            else arrived = -1;
            double dt = 8, cap = 3.0 * dt * Math.Clamp(speed?.Invoke() ?? 1, 0.1, 50);
            double step = Math.Min(d, Math.Min(cap, d * dt / 40 + 0.5));
            int x = (int)Math.Round(c.X + (t.X - c.X) / d * step), y = (int)Math.Round(c.Y + (t.Y - c.Y) / d * step);
            var mv = new MacroEvent(0, EventKind.MouseMove, X: x, Y: y);
            inner.Send(in mv);
            _cursor = (x, y);
            Pause(dt);
        }
        return null;                         // stopped, or it never came to rest: nothing was landed on
    }

    bool RepeatBlocked(string id, Point p, int minRepeatMs) =>
        minRepeatMs > 0 && _lastClick.TryGetValue(id, out var l) && clock.NowMs - l.At < minRepeatMs && Dist(l.P, p) <= StableRadius;

    /// <summary>
    /// Move the cursor from where it is to the click point with a minimum-jerk curve (slow start, fast middle, slow end) in small steps,
    /// re-aiming while it travels in case the target is still moving. Duration grows with distance (about 0.12 s + 0.45 ms per pixel, at most
    /// 0.9 s) and shrinks with the playback speed. Stops at once on a stop request.
    /// </summary>
    void Glide(Point first, Func<Point> aim, int glideMs)
    {
        var from = (correctCursor ? readCursor?.Invoke() : null) ?? _cursor ?? readCursor?.Invoke();   // the real pointer, not where we think we left it
        if (from is not { } start) return;
        double dist = Dist(new Point(start.X, start.Y), first);
        if (dist < 4) return;
        double ms = glideMs > 0 ? glideMs : Math.Clamp(120 + dist * 0.45, 120, 900);
        ms /= Math.Clamp(speed?.Invoke() ?? 1, 0.1, 50);
        int steps = Math.Max(3, (int)(ms / 8));
        var dest = first;
        double ox = start.X, oy = start.Y, s0 = 0;                    // the curve is measured from the last re-aim, so a moved target bends the path without a jump
        for (int k = 1; k <= steps && !stopRequested(); k++)
        {
            double t = (double)k / steps, s = t * t * t * (10 - 15 * t + 6 * t * t);
            if (k % 4 == 0 && aim() is var na && (na.X != dest.X || na.Y != dest.Y))
            {
                double sp = (double)(k - 1) / steps; sp = sp * sp * sp * (10 - 15 * sp + 6 * sp * sp);
                ox = _cursor?.X ?? ox; oy = _cursor?.Y ?? oy; s0 = sp; dest = na;
            }
            double f = s0 >= 1 ? 1 : (s - s0) / (1 - s0);
            int x = (int)Math.Round(ox + (dest.X - ox) * f), y = (int)Math.Round(oy + (dest.Y - oy) * f);
            var mv = new MacroEvent(0, EventKind.MouseMove, X: x, Y: y);
            inner.Send(in mv);
            _cursor = (x, y);
            Pause(ms / steps);
        }
    }

    /// <summary>
    /// Closed loop: move there, read where the pointer really is, and if it is off (display scaling, monitor layout, another monitor's
    /// resolution) shift the injected position by the error. Repeats up to 3 times. Returns the position to inject for the click.
    /// </summary>
    (int X, int Y) Land(int x, int y)
    {
        if (!correctCursor || readCursor is null) return (x, y);
        int ix = x, iy = y;
        for (int i = 0; i < 3 && !stopRequested(); i++)
        {
            var mv = new MacroEvent(0, EventKind.MouseMove, X: ix, Y: iy);
            inner.Send(in mv);
            Pause(3);
            var c = readCursor();
            int ex = x - c.X, ey = y - c.Y;
            if (Math.Abs(ex) <= 1 && Math.Abs(ey) <= 1) return (ix, iy);
            if (i == 0) Log("cursor", LogResult.Warn, $"the pointer landed {Math.Abs(ex)} px {(ex < 0 ? "right" : "left")}, {Math.Abs(ey)} px {(ey < 0 ? "below" : "above")} the target point: correcting", null, x, y);
            ix += ex; iy += ey;
        }
        return (ix, iy);
    }

    void Press(MouseBtn button, int x, int y)
    {
        _cursor = (x, y);
        (x, y) = Land(x, y);
        var down = new MacroEvent(0, EventKind.ButtonDown, Button: button, X: x, Y: y);
        inner.Send(in down);
        try { Pause(HoldMs, ignoreStop: true); }
        finally { var up = down with { Kind = EventKind.ButtonUp }; inner.Send(in up); }
    }

    void Pause(double ms, bool ignoreStop = false)
    {
        double until = clock.NowMs + ms;
        for (double now = clock.NowMs; now < until && (ignoreStop || !stopRequested()); now = clock.NowMs) clock.Sleep(until - now);
    }

    static double Dist(Point a, Point b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    void Log(string what, LogResult r, string msg, double? conf = null, int? x = null, int? y = null, double ms = 0) =>
        log?.Add(currentIndex?.Invoke() ?? -1, what, r, msg, conf, x, y, ms);
}

/// <summary>Where to click, relative to the target as it is detected right now.</summary>
public static class ClickPoint
{
    /// <summary>
    /// centre + (RelX × width, RelY × height) + pixel offset, then, if that is off the detected target, the target's centre. An anchor or offset
    /// that lies far outside it (a click recorded away from the target, a stale absolute offset) would otherwise click something else entirely.
    /// </summary>
    public static Point Compute(Detection hit, StepOptions? o, int offsetX = 0, int offsetY = 0)
    {
        var r = Raw(hit, o, offsetX, offsetY);
        var b = hit.Rect;
        // a stored point off the button (stray offset, click recorded elsewhere) would hit something else: click the button's centre instead
        return r.X >= b.X && r.X <= b.Right && r.Y >= b.Y && r.Y <= b.Bottom ? r : hit.Center;
    }

    /// <summary>The click point exactly as stored, even when it lies outside the target.</summary>
    public static Point Raw(Detection hit, StepOptions? o, int offsetX = 0, int offsetY = 0)
    {
        var c = hit.Center;
        double rx = o?.RelX ?? 0, ry = o?.RelY ?? 0;
        return new Point((int)Math.Round(c.X + rx * hit.Rect.Width + offsetX), (int)Math.Round(c.Y + ry * hit.Rect.Height + offsetY));
    }

    /// <summary>
    /// The anchor of an originally recorded click: how far from the target centre it was, as a fraction of the target size.
    /// Clicks outside the target are clamped to 2× its size so a stray click does not become an absurd anchor.
    /// </summary>
    public static (double RelX, double RelY) FromRecorded(ScreenRect target, int clickX, int clickY)
    {
        double w = Math.Max(1, target.Width), h = Math.Max(1, target.Height);
        double rx = (clickX - (target.X + w / 2)) / w, ry = (clickY - (target.Y + h / 2)) / h;
        return (Math.Clamp(rx, -2, 2), Math.Clamp(ry, -2, 2));
    }
}
