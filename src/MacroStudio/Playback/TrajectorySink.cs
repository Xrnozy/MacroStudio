using MacroStudio.Model;
using OpenCvSharp;

namespace MacroStudio.Playback;

/// <summary>
/// Bends the recorded approach to an adaptive click so it ends on the target's CURRENT position. Works only on the events passing through
/// during playback; the recording itself is never touched.
/// <para>
/// For a move with progress s (0 at the start of the approach, 1 at the end) the position is shifted by delta × smoothstep(s), where
/// delta = (where the click will land now) − (where it was recorded). So the start of the path is exactly as recorded, the shape and timing
/// are kept, the end lands on the target, and a target that has not moved gives delta 0 and the exact recorded path. Delta is refreshed on
/// every move while the target is visible, so a target that is still moving is followed.
/// </para>
/// </summary>
public sealed class TrajectorySink(IInputSink inner, IReadOnlyList<MacroEvent> events, TrajectoryPlan plan, ITargetFinder finder,
    Func<int> currentIndex, Func<(int X, int Y), (int X, int Y)> mapPoint, Func<bool> enabled, ExecutionLog? log = null, Func<double>? speed = null,
    Func<(int X, int Y), (int X, int Y)>? clamp = null) : IInputSink
{
    readonly Dictionary<int, (double X, double Y)> _delta = new();
    readonly HashSet<int> _logged = new();
    readonly Dictionary<(int, double), double> _room = new();

    /// <summary>
    /// The fastest the pointer may travel while the recorded approach is bent, in px per ms (3 px/ms = 3000 px/s: a quick human flick).
    /// The recorded approach lasts a fixed time, so a target that moved very far cannot be reached inside it at a natural speed:
    /// the bend is limited to what fits, and the adaptive click glides the rest of the way at a natural speed.
    /// </summary>
    public const double MaxSpeedPxPerMs = 3.0;

    // After a click on a target that is not where it was recorded, the recording goes on from the RECORDED click point. Shifting the
    // movements that follow by the difference makes them continue from where the cursor really is; the shift fades out along the path.
    (double X, double Y) _carry;
    double _carryTravelled, _carryLength = 1;
    (int X, int Y)? _prevRecorded;
    int _lastIndex = -1;
    bool _dropTail;   // after the last optimized click only mouse moves remain: they are not replayed, the pointer glides to the recorded end instead

    /// <summary>Share of the carried shift still applied (1 right after the click, 0 once the recorded path has moved on far enough).</summary>
    double CarryWeight => _carry == default ? 0 : 1 - Smooth(_carryTravelled / _carryLength);

    public void Send(in MacroEvent e)
    {
        int i = currentIndex();
        if (i < _lastIndex) { _carry = default; _prevRecorded = null; _delta.Clear(); _dropTail = false; }   // the macro looped back to its start
        _lastIndex = i;
        if (!enabled()) { inner.Send(in e); return; }

        if (e.Kind == EventKind.TargetClick)
        {
            inner.Send(in e);
            Carry(i, e);
            bool onlyMoves = i >= 0 && i < events.Count;
            for (int k = i + 1; onlyMoves && k < events.Count; k++) if (events[k].Kind != EventKind.MouseMove) onlyMoves = false;
            _dropTail = onlyMoves && (e.Step?.Move ?? MoveMode.FollowPath) == MoveMode.Optimized;
            return;
        }
        if (e.HasPos() && !e.IsTarget())
        {
            if (_dropTail && e.Kind == EventKind.MouseMove) return;
            if (e.Kind != EventKind.MouseMove) { _carry = default; inner.Send(in e); return; }   // a recorded click or scroll lands exactly where it was recorded
            {
                if (_prevRecorded is { } p0 && _carry != default) _carryTravelled += Math.Sqrt((double)(e.X - p0.X) * (e.X - p0.X) + (double)(e.Y - p0.Y) * (e.Y - p0.Y));
                _prevRecorded = (e.X, e.Y);
            }
            double cw = CarryWeight;
            double sx = _carry.X * cw, sy = _carry.Y * cw;
            if (e.Kind == EventKind.MouseMove && i >= 0 && i < events.Count && plan.TryGet(i, out var a))
            {
                if (plan.IsOptimized(a.Click)) return;          // the cursor goes straight to the target from wherever it is (see StepExecutor.Glide)
                var (dx, dy) = DeltaFor(a.Click);
                double w = Smooth(a.Progress);
                sx = sx * (1 - w) + dx * w; sy = sy * (1 - w) + dy * w;   // the carried shift hands over to the bend towards the target
            }
            if (sx != 0 || sy != 0)
            {
                var pt = (X: e.X + (int)Math.Round(sx), Y: e.Y + (int)Math.Round(sy));
                if (clamp != null) pt = clamp(pt);
                var moved = e with { X = pt.X, Y = pt.Y };
                inner.Send(in moved);
                return;
            }
        }
        inner.Send(in e);
    }

    void Carry(int index, in MacroEvent step)
    {
        _carry = default; _carryTravelled = 0; _prevRecorded = null;
        if (inner is not IClickReporter { LastClick: { } landed }) return;
        (int X, int Y)? orig = index >= 0 && index < events.Count && plan.HasApproach(index) ? plan.OriginalOf(index)
            : step.Step is { } s && (s.FallbackX != 0 || s.FallbackY != 0) ? (s.FallbackX, s.FallbackY) : null;
        if (orig is not { } o) return;
        var m = mapPoint(o);
        double dx = landed.X - m.X, dy = landed.Y - m.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 2) return;
        // the shift must be gone by the next recorded click (it targets a fixed spot), so it fades within the path that leads there
        double room = 0, reach = double.MaxValue; var at = o;
        for (int k = index + 1; k < events.Count; k++)
        {
            var n = events[k];
            if (n.Kind == EventKind.TargetClick || (n.HasPos() && n.Kind != EventKind.MouseMove)) { reach = room; break; }
            if (n.Kind == EventKind.MouseMove) { room += Math.Sqrt((double)(n.X - at.X) * (n.X - at.X) + (double)(n.Y - at.Y) * (n.Y - at.Y)); at = (n.X, n.Y); }
        }
        if (reach < 1) return;
        _carry = (dx, dy);
        _carryLength = Math.Min(Math.Max(150, 1.5 * len), reach);
        log?.Add(index, "mouse path", LogResult.Ok, $"the recording continues from the click point: later movements shifted by ({dx:+0;-0;0}, {dy:+0;-0;0}) px, fading out over {_carryLength:0} px of path", null, landed.X, landed.Y);
    }

    /// <summary>How far the target's click point is from where the recording clicked, in screen pixels (refreshed while the target is visible).</summary>
    (double X, double Y) DeltaFor(int click)
    {
        var step = events[click];
        if (step.Target is { } id && finder.Find(id) is { } sight)
        {
            var now = ClickPoint.Compute(sight.Hit, step.Step, step.X, step.Y);
            var (ox, oy) = mapPoint(plan.OriginalOf(click));
            var d = Capped(click, (now.X - ox, now.Y - oy));
            if (_logged.Add(click) && (Math.Abs(d.Item1) >= 2 || Math.Abs(d.Item2) >= 2))
                log?.Add(click, "mouse path", LogResult.Ok, $"recorded approach bent by ({d.Item1:+0;-0;0}, {d.Item2:+0;-0;0}) px to follow the target", sight.Score, now.X, now.Y);
            return _delta[click] = d;
        }
        return _delta.TryGetValue(click, out var last) ? last : (0, 0);   // target not visible right now: keep the last bend, never snap back
    }

    /// <summary>Limit how much the path is bent so that no part of it needs more than the natural speed (taking the playback speed into account).</summary>
    (double, double) Capped(int click, (double X, double Y) d)
    {
        double sp = Math.Round(speed?.Invoke() ?? 1, 2);
        if (!_room.TryGetValue((click, sp), out double room))      // how far the path can be bent before some step of it would be too fast (computed once per click and speed)
            _room[(click, sp)] = room = plan.MaxBend(click, sp, MaxSpeedPxPerMs);
        double want = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (want <= 0 || want <= room) return d;
        double f = Math.Max(0, room) / want;
        return (d.X * f, d.Y * f);
    }

    /// <summary>Ease in: the first part of the approach follows the recording exactly, the bend builds smoothly towards the end.</summary>
    internal static double Smooth(double s) { s = Math.Clamp(s, 0, 1); return s * s * (3 - 2 * s); }
}
