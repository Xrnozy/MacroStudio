using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Vision;
using OpenCvSharp;
using static MacroStudio.Tests.CoreTests;

namespace MacroStudio.Tests;

public class TrajectoryTests
{
    sealed class Fin : ITargetFinder
    {
        public Func<(int X, int Y)?> Center = () => null;     // where the 80x40 target is right now (null = not visible)
        long _seq;
        public string? NameOf(string id) => "T";
        public double ThresholdOf(string id) => 0.85;
        public Sighting? Find(string id) =>
            Center() is { } c ? new Sighting(new Detection(new Rect(c.X - 40, c.Y - 20, 80, 40), 0.97, 1), ++_seq, DateTime.UtcNow) : null;
    }

    sealed class Collect : IInputSink
    {
        public readonly List<MacroEvent> Got = new();
        public void Send(in MacroEvent e) => Got.Add(e);
    }

    /// <summary>A recorded approach: the pointer travels along a gentle curve from (100,100) to (500,300), then clicks the target there.</summary>
    static MacroEvent[] Recording(MoveMode mode = MoveMode.FollowPath, int moves = 10, int stepMs = 80)
    {
        var l = new List<MacroEvent> { new(0, EventKind.KeyDown, 0x41, 0x1E), new(10, EventKind.KeyUp, 0x41, 0x1E) };
        for (int k = 0; k < moves; k++)
        {
            double t = k / (double)(moves - 1);
            l.Add(new MacroEvent(100 + k * stepMs, EventKind.MouseMove, X: (int)(100 + 400 * t), Y: (int)(100 + 200 * t + 30 * Math.Sin(t * Math.PI))));
        }
        var last = l[^1];
        l.Add(new MacroEvent(last.T + 40, EventKind.TargetClick, Target: "t", Delta: 5000, Step: new StepOptions { Move = mode, FallbackX = last.X, FallbackY = last.Y }));
        return l.ToArray();
    }

    static (MacroEvent[] Moves, int First) Replay(MacroEvent[] ev, Fin fin, bool enabled = true, Func<(int, int), (int, int)>? map = null, Action<int>? beforeEach = null)
    {
        var plan = TrajectoryPlan.Build(ev);
        var outp = new Collect();
        int idx = -1;
        var sink = new TrajectorySink(outp, ev, plan, fin, () => idx, p => map?.Invoke(p) ?? p, () => enabled);
        int first = Array.FindIndex(ev, e => e.Kind == EventKind.MouseMove);
        for (int i = first; i < ev.Length && ev[i].Kind == EventKind.MouseMove; i++)
        {
            idx = i; beforeEach?.Invoke(i);
            sink.Send(ev[i]);
        }
        return (outp.Got.ToArray(), first);
    }

    // ---- which moves are the approach ----

    [Fact]
    public void The_plan_marks_only_the_moves_right_before_a_follow_path_click_with_progress_by_distance()
    {
        var ev = Recording();
        var plan = TrajectoryPlan.Build(ev);
        Assert.Equal(10, plan.ApproachMoves);
        Assert.False(plan.TryGet(0, out _));                           // the keystrokes before it are not part of the approach
        Assert.False(plan.TryGet(ev.Length - 1, out _));
        Assert.True(plan.TryGet(2, out var a0)); Assert.Equal(0, a0.Progress, 6);
        Assert.True(plan.TryGet(11, out var a1)); Assert.Equal(1, a1.Progress, 6);
        double prev = -1;
        for (int i = 2; i <= 11; i++) { plan.TryGet(i, out var a); Assert.True(a.Progress >= prev); prev = a.Progress; Assert.Equal(ev.Length - 1, a.Click); }
        Assert.Equal((ev[11].X, ev[11].Y), plan.OriginalOf(ev.Length - 1));   // heading for where the path ended (here also the stored position)
    }

    [Fact]
    public void Jump_clicks_other_inputs_and_old_recordings_get_no_plan()
    {
        Assert.Equal(0, TrajectoryPlan.Build(Recording(MoveMode.Jump)).ApproachMoves);
        Assert.Equal(0, TrajectoryPlan.Build(Recording(MoveMode.Glide)).ApproachMoves);

        var ev = Recording().ToList();
        ev.Insert(8, new MacroEvent(ev[7].T + 1, EventKind.Wheel, X: 1, Y: 1, Delta: -120));   // a scroll in the middle ends the approach
        var plan = TrajectoryPlan.Build(ev);
        Assert.False(plan.TryGet(3, out _));
        Assert.True(plan.TryGet(9, out _));

        var slow = Recording().ToList();
        slow[5] = slow[5] with { T = slow[5].T - 9000 };                                        // a long pause: that move is too old to belong to it
        Assert.True(TrajectoryPlan.Build(slow).ApproachMoves < 10);
    }

    // ---- the bend ----

    [Fact]
    public void A_target_that_has_not_moved_gives_exactly_the_recorded_path()
    {
        var ev = Recording();
        var fin = new Fin { Center = () => (500, 300) };               // where the click was recorded
        var (outp, first) = Replay(ev, fin);
        for (int k = 0; k < outp.Length; k++) Assert.Equal((ev[first + k].X, ev[first + k].Y), (outp[k].X, outp[k].Y));
    }

    [Fact]
    public void A_moved_target_bends_the_path_to_end_on_it_keeping_start_shape_and_timing()
    {
        var ev = Recording();
        var snapshot = ev.ToArray();
        var fin = new Fin { Center = () => (1200, 700) };              // moved by (+700, +400)
        var (outp, first) = Replay(ev, fin);

        Assert.Equal((ev[first].X, ev[first].Y), (outp[0].X, outp[0].Y));                         // the start is exactly as recorded
        Assert.Equal((1200, 700), (outp[^1].X, outp[^1].Y));                                      // the end is on the target
        Assert.Equal(ev.Skip(first).Take(outp.Length).Select(e => e.T), outp.Select(e => e.T));   // timing untouched
        var plan = TrajectoryPlan.Build(ev);
        for (int k = 0; k < outp.Length; k++)
        {
            plan.TryGet(first + k, out var a);
            double w = a.Progress * a.Progress * (3 - 2 * a.Progress);
            Assert.InRange(outp[k].X - ev[first + k].X, 700 * w - 1, 700 * w + 1);                // each point is shifted by an eased share of the move
            Assert.InRange(outp[k].Y - ev[first + k].Y, 400 * w - 1, 400 * w + 1);
        }
        Assert.Equal(snapshot, ev);                                                               // the recording itself was not touched
    }

    [Fact]
    public void The_anchor_is_respected_when_bending()
    {
        // recorded click was at the target's bottom-right corner; the target has moved: the path must end at ITS bottom-right corner
        var ev = Recording().ToArray();
        ev[^1] = ev[^1] with { Step = ev[^1].Step! with { RelX = 0.5, RelY = 0.5 } };
        var fin = new Fin { Center = () => (900, 600) };
        var (outp, _) = Replay(ev, fin);
        Assert.Equal((900 + 40, 600 + 20), (outp[^1].X, outp[^1].Y));
    }

    [Fact]
    public void A_target_that_is_not_visible_leaves_the_path_alone_and_a_vanished_one_keeps_the_last_bend()
    {
        var ev = Recording();
        var (plain, first) = Replay(ev, new Fin());
        for (int k = 0; k < plain.Length; k++) Assert.Equal((ev[first + k].X, ev[first + k].Y), (plain[k].X, plain[k].Y));

        bool visible = true;
        var fin = new Fin { Center = () => visible ? (700, 500) : null };
        var (outp, _) = Replay(ev, fin, beforeEach: i => { if (i >= first + 6) visible = false; });   // it disappears for the last part of the approach
        // the full bend is what the target's last known position asked for: (700,500) minus the recorded click (500,300)
        var plan = TrajectoryPlan.Build(ev);
        for (int k = 6; k < 10; k++)
        {
            plan.TryGet(first + k, out var a);
            double w = a.Progress * a.Progress * (3 - 2 * a.Progress);
            Assert.InRange(outp[k].X - ev[first + k].X, 200 * w - 1, 200 * w + 1);   // it did not snap back to the unbent path once the target vanished
        }
        Assert.Equal((700, 500), (outp[9].X, outp[9].Y));                           // and the approach still ends where the target was last seen
    }

    [Fact]
    public void Switched_off_every_path_is_exactly_as_recorded()
    {
        var ev = Recording();
        var (outp, first) = Replay(ev, new Fin { Center = () => (1200, 700) }, enabled: false);
        for (int k = 0; k < outp.Length; k++) Assert.Equal((ev[first + k].X, ev[first + k].Y), (outp[k].X, outp[k].Y));
    }

    [Fact]
    public void Positions_remapped_for_another_screen_are_bent_relative_to_the_remapped_recording()
    {
        // the adapter shifted every recorded point by +100 px in x (another monitor layout); the target sits where the remapped click would be
        var ev = Recording();
        var shifted = ev.Select(e => e.Kind == EventKind.MouseMove ? e with { X = e.X + 100 } : e).ToArray();   // what reaches this sink after remapping
        var plan = TrajectoryPlan.Build(ev);
        var outp = new Collect(); int idx = -1;
        var sink = new TrajectorySink(outp, ev, plan, new Fin { Center = () => (600, 300) }, () => idx, p => (p.X + 100, p.Y), () => true);
        for (int i = 2; i < 12; i++) { idx = i; sink.Send(shifted[i]); }
        for (int k = 0; k < outp.Got.Count; k++) Assert.Equal((shifted[2 + k].X, shifted[2 + k].Y), (outp.Got[k].X, outp.Got[k].Y));   // delta 0: untouched
    }

    [Fact]
    public void Moves_outside_an_approach_and_other_events_pass_through()
    {
        var ev = Recording(MoveMode.Jump);
        var (outp, first) = Replay(ev, new Fin { Center = () => (1200, 700) });
        for (int k = 0; k < outp.Length; k++) Assert.Equal((ev[first + k].X, ev[first + k].Y), (outp[k].X, outp[k].Y));
        var o = new Collect(); int idx = 0;
        new TrajectorySink(o, ev, TrajectoryPlan.Build(ev), new Fin(), () => idx, p => p, () => true).Send(ev[0]);
        Assert.Single(o.Got);
    }

    // ---- gliding ----

    static (StepExecutor Ex, RecordingSink Out, FakeClock Clock, ExecutionLog Log) Rig(Fin fin, Func<bool>? dynamicPath = null, Func<double>? speed = null, Func<bool>? stop = null, (int, int)? cursor = null)
    {
        var clock = new FakeClock(); var inner = new RecordingSink(clock); var log = new ExecutionLog();
        return (new StepExecutor(inner, fin, clock, stop ?? (() => false), log, () => 3, () => true, speed, dynamicPath, () => cursor ?? (100, 100)), inner, clock, log);
    }

    static MacroEvent Click(MoveMode mode, int glideMs = 0) =>
        new(0, EventKind.TargetClick, Delta: 3000, Target: "t", Button: MouseBtn.Left, Step: new StepOptions { Move = mode, GlideMs = glideMs, RequireStable = false, Retries = 0, MinRepeatMs = 0 });

    [Fact]
    public void Glide_moves_smoothly_from_the_cursor_to_the_target_and_clicks_exactly_there()
    {
        var r = Rig(new Fin { Center = () => (900, 500) });
        r.Ex.Send(Click(MoveMode.Glide));
        var moves = r.Out.Sent.Where(s => s.E.Kind == EventKind.MouseMove).ToList();
        var down = r.Out.Sent.First(s => s.E.Kind == EventKind.ButtonDown);
        Assert.True(moves.Count > 10, $"expected a smooth path, got {moves.Count} steps");
        Assert.Equal((900, 500), (down.E.X, down.E.Y));
        double prev = double.MaxValue;
        foreach (var m in moves)
        {
            double d = Math.Sqrt(Math.Pow(900 - m.E.X, 2) + Math.Pow(500 - m.E.Y, 2));
            Assert.True(d <= prev + 0.5); prev = d;                      // it only ever gets closer
        }
        double ms = down.At - moves[0].At;
        Assert.InRange(ms, 300, 700);                                     // ~0.12 s + 0.45 ms/px for ~860 px
        Assert.True(moves[0].At >= 0);
    }

    [Fact]
    public void Glide_time_grows_with_distance_and_shrinks_with_playback_speed()
    {
        double Duration(int tx, double speed)
        {
            var r = Rig(new Fin { Center = () => (tx, 100) }, speed: () => speed);
            r.Ex.Send(Click(MoveMode.Glide));
            var moves = r.Out.Sent.Where(s => s.E.Kind == EventKind.MouseMove).ToList();
            return r.Out.Sent.First(s => s.E.Kind == EventKind.ButtonDown).At - moves[0].At;
        }
        double near = Duration(160, 1), far = Duration(1700, 1), fast = Duration(1700, 4);
        Assert.True(far > near * 2, $"near {near}, far {far}");
        Assert.True(fast < far / 2, $"far {far}, at 4x {fast}");
        var fixedMs = Rig(new Fin { Center = () => (1700, 100) });
        fixedMs.Ex.Send(Click(MoveMode.Glide, glideMs: 200));
        var m = fixedMs.Out.Sent.Where(s => s.E.Kind == EventKind.MouseMove).ToList();
        Assert.InRange(fixedMs.Out.Sent.First(s => s.E.Kind == EventKind.ButtonDown).At - m[0].At, 150, 260);   // an explicit duration is honoured
    }

    [Fact]
    public void Jump_mode_and_the_global_switch_keep_the_instant_jump()
    {
        var jump = Rig(new Fin { Center = () => (900, 500) });
        jump.Ex.Send(Click(MoveMode.Jump));
        Assert.DoesNotContain(jump.Out.Sent, s => s.E.Kind == EventKind.MouseMove);
        Assert.Equal(2, jump.Out.Sent.Count);

        var off = Rig(new Fin { Center = () => (900, 500) }, dynamicPath: () => false);
        off.Ex.Send(Click(MoveMode.Glide));                              // the master switch overrides the step
        Assert.DoesNotContain(off.Out.Sent, s => s.E.Kind == EventKind.MouseMove);
    }

    [Fact]
    public void Follow_path_with_no_recorded_approach_glides_and_a_cursor_already_there_does_not_move()
    {
        var r = Rig(new Fin { Center = () => (900, 500) });
        r.Ex.Send(Click(MoveMode.FollowPath));
        Assert.Contains(r.Out.Sent, s => s.E.Kind == EventKind.MouseMove);

        var there = Rig(new Fin { Center = () => (900, 500) }, cursor: (901, 500));
        there.Ex.Send(Click(MoveMode.FollowPath));
        Assert.DoesNotContain(there.Out.Sent, s => s.E.Kind == EventKind.MouseMove);
    }

    [Fact]
    public void Glide_follows_a_target_that_is_still_moving_and_stops_on_request()
    {
        int polls = 0;
        var moving = new Fin { Center = () => (900 + Math.Min(polls++ / 3, 200), 500) };   // slides right while the cursor travels
        var r = Rig(moving);
        r.Ex.Send(Click(MoveMode.Glide));
        var down = r.Out.Sent.First(s => s.E.Kind == EventKind.ButtonDown).E;
        Assert.True(down.X > 900, "the click should land where the target ended up, not where it was when the glide began");

        bool stop = false;
        var s2 = Rig(new Fin { Center = () => (1800, 900) }, stop: () => stop);
        s2.Out.OnSend = n => { if (n >= 5) stop = true; };
        s2.Ex.Send(Click(MoveMode.Glide));
        Assert.True(s2.Out.Sent.Count(s => s.E.Kind == EventKind.MouseMove) <= 6);
        Assert.DoesNotContain(s2.Out.Sent, s => s.E.Kind == EventKind.ButtonDown);
    }

    // ---- natural speed ----

    static double MaxSpeed(IReadOnlyList<(double At, int X, int Y)> pts)
    {
        double max = 0;
        for (int k = 1; k < pts.Count; k++)
        {
            double dt = pts[k].At - pts[k - 1].At;
            if (dt <= 0) continue;
            max = Math.Max(max, Math.Sqrt(Math.Pow(pts[k].X - pts[k - 1].X, 2) + Math.Pow(pts[k].Y - pts[k - 1].Y, 2)) / dt);
        }
        return max;
    }

    [Fact]
    public void A_target_that_moved_very_far_limits_the_bend_to_a_natural_speed()
    {
        var ev = Recording(stepMs: 20);                                   // the user approached in just 180 ms
        var fin = new Fin { Center = () => (2700, 900) };                 // ...and the target is now ~2500 px further away
        var clock = new FakeClock(); var inner = new RecordingSink(clock);
        var plan = TrajectoryPlan.Build(ev);
        int idx = -1;
        var sink = new TrajectorySink(inner, ev, plan, fin, () => idx, p => p, () => true);
        int first = Array.FindIndex(ev, e => e.Kind == EventKind.MouseMove);
        for (int i = first; ev[i].Kind == EventKind.MouseMove; i++) { clock.Now = ev[i].T; idx = i; sink.Send(ev[i]); }

        var pts = inner.Sent.Select(s => (s.At, s.E.X, s.E.Y)).ToList();
        // the limit is the natural speed, or 25% above the user's own fastest recorded step if they already moved faster
        var recorded = ev.Where(e => e.Kind == EventKind.MouseMove).Select(e => (e.T, e.X, e.Y)).ToList();
        double recordedPeak = MaxSpeed(recorded);
        double limit = Math.Max(TrajectorySink.MaxSpeedPxPerMs, 1.25 * recordedPeak) + 0.1;
        Assert.True(MaxSpeed(pts) <= limit, $"the bent approach reached {MaxSpeed(pts):0.0} px/ms (recorded peak {recordedPeak:0.0}, limit {limit:0.0})");
        var end = inner.Sent[^1].E;
        Assert.True(Math.Sqrt(Math.Pow(2700 - end.X, 2) + Math.Pow(900 - end.Y, 2)) > 1000, "the rest of the distance is left to the glide, not squeezed into the recorded time");
        Assert.Equal((ev[first].X, ev[first].Y), (inner.Sent[0].E.X, inner.Sent[0].E.Y));
    }

    [Fact]
    public void Bend_and_glide_together_reach_a_far_target_without_ever_moving_too_fast_and_leave_the_recording_alone()
    {
        var ev = Recording(stepMs: 20);
        var snapshot = ev.ToArray();
        var fin = new Fin { Center = () => (2700, 900) };
        var clock = new FakeClock(); var inner = new RecordingSink(clock); var log = new ExecutionLog();
        Player? player = null;
        var exec = new StepExecutor(inner, fin, clock, () => player?.StopRequested == true, log, () => player?.CurrentIndex ?? -1, () => true, () => 1, () => true, () => (100, 100));
        var chain = new TrajectorySink(exec, ev, TrajectoryPlan.Build(ev), fin, () => player?.CurrentIndex ?? -1, p => p, () => true, log);
        player = new Player(chain, clock) { Log = log };
        player.Run(ev, 0, ev.Length, 1);

        Assert.Null(player.Error);
        var pts = inner.Sent.Where(s => s.E.Kind is EventKind.MouseMove or EventKind.ButtonDown).Select(s => (s.At, s.E.X, s.E.Y)).ToList();
        var down = inner.Sent.First(s => s.E.Kind == EventKind.ButtonDown).E;
        Assert.Equal((2700, 900), (down.X, down.Y));                       // the click is on the target's current position
        Assert.True(MaxSpeed(pts) <= 5.0, $"fastest movement {MaxSpeed(pts):0.0} px/ms");   // a jump would be thousands of px in one step
        Assert.True(inner.Sent.Count(s => s.E.Kind == EventKind.MouseMove) > 20);            // smooth: many small steps
        Assert.Equal(snapshot, ev);                                         // playback never alters the recorded events
        Assert.Contains(log.Snapshot(), x => x.What == "mouse path" && x.Message.Contains("bent"));
    }

    [Fact]
    public void With_the_global_switch_off_the_recorded_path_is_replayed_exactly_then_the_cursor_jumps()
    {
        var ev = Recording(stepMs: 20);
        var fin = new Fin { Center = () => (2700, 900) };
        var clock = new FakeClock(); var inner = new RecordingSink(clock);
        Player? player = null;
        var exec = new StepExecutor(inner, fin, clock, () => false, null, () => player?.CurrentIndex ?? -1, () => true, () => 1, () => false, () => (100, 100));
        var chain = new TrajectorySink(exec, ev, TrajectoryPlan.Build(ev), fin, () => player?.CurrentIndex ?? -1, p => p, () => false);
        player = new Player(chain, clock);
        player.Run(ev, 0, ev.Length, 1);

        var moves = inner.Sent.Where(s => s.E.Kind == EventKind.MouseMove).Select(s => (s.E.X, s.E.Y)).ToList();
        Assert.Equal(ev.Where(e => e.Kind == EventKind.MouseMove).Select(e => (e.X, e.Y)), moves);   // exactly the recorded moves, nothing added or bent
        var down = inner.Sent.First(s => s.E.Kind == EventKind.ButtonDown).E;
        Assert.Equal((2700, 900), (down.X, down.Y));                                                  // then the instant jump to the target, as before
    }

    // ---- optimized ----

    [Fact]
    public void Optimized_skips_the_recorded_approach_and_goes_straight_to_the_target_leaving_the_recording_alone()
    {
        var ev = Recording(MoveMode.Optimized);
        var snapshot = ev.ToArray();
        var plan = TrajectoryPlan.Build(ev);
        Assert.True(plan.IsOptimized(ev.Length - 1));
        var (sent, _) = Replay(ev, new Fin { Center = () => (900, 500) });
        Assert.Empty(sent);                                              // none of the wandering approach is replayed
        Assert.Equal(snapshot, ev);

        var r = Rig(new Fin { Center = () => (900, 500) }, cursor: (100, 100));
        r.Ex.Send(ev[^1]);                                               // the click itself
        var moves = r.Out.Sent.Where(s => s.E.Kind == EventKind.MouseMove).ToList();
        double line = Math.Sqrt(800.0 * 800 + 400 * 400), walked = 0; (int X, int Y) p = (100, 100); double maxOff = 0;
        foreach (var m in moves)
        {
            walked += Math.Sqrt(Math.Pow(m.E.X - p.X, 2) + Math.Pow(m.E.Y - p.Y, 2)); p = (m.E.X, m.E.Y);
            maxOff = Math.Max(maxOff, Math.Abs((m.E.X - 100) * 400.0 - (m.E.Y - 100) * 800) / line);   // distance from the straight line
        }
        Assert.InRange(walked, line - 2, line + 2);                      // no wasted movement
        Assert.True(maxOff < 2, $"off the straight line by {maxOff:0.0}px");
        Assert.Equal((900, 500), (r.Out.Sent.First(s => s.E.Kind == EventKind.ButtonDown).E.X, r.Out.Sent.First(s => s.E.Kind == EventKind.ButtonDown).E.Y));
    }

    [Fact]
    public void A_bare_click_step_with_a_looping_recording_goes_straight_to_the_target_instead_of_replaying_the_loop()
    {
        // the shape of a real saved macro: the user circled around, then the click has no step settings at all
        var l = new List<MacroEvent>();
        for (int k = 0; k < 60; k++) l.Add(new MacroEvent(k * 20, EventKind.MouseMove, X: (int)(500 + 300 * Math.Cos(k / 60.0 * 6.28)), Y: (int)(500 + 300 * Math.Sin(k / 60.0 * 6.28))));
        l.Add(new MacroEvent(1300, EventKind.TargetClick, Target: "t", Delta: 3000, Button: MouseBtn.Left, Step: new StepOptions { Move = MoveMode.Optimized }));   // the default is now FollowPath (the recorded path); this test is about the explicit direct mode
        var ev = l.ToArray();
        var (sent, _) = Replay(ev, new Fin { Center = () => (900, 500) });
        Assert.Empty(sent);                                                  // not one point of the loop is replayed
        var r = Rig(new Fin { Center = () => (900, 500) }, cursor: (100, 100));
        r.Ex.Send(ev[^1]);
        double walked = 0; (int X, int Y) p = (100, 100);
        foreach (var m in r.Out.Sent.Where(s => s.E.Kind == EventKind.MouseMove)) { walked += Math.Sqrt(Math.Pow(m.E.X - p.X, 2) + Math.Pow(m.E.Y - p.Y, 2)); p = (m.E.X, m.E.Y); }
        double line = Math.Sqrt(800.0 * 800 + 400 * 400);
        Assert.InRange(walked, line - 2, line * 1.03);
    }

    [Fact]
    public void A_target_that_moves_while_the_cursor_travels_bends_the_path_without_any_jump()
    {
        int polls = 0;
        var fin = new Fin { Center = () => (900, 500 + Math.Min(polls++ * 8, 300)) };         // slides down the whole time
        var r = Rig(fin, cursor: (100, 100));
        r.Ex.Send(Click(MoveMode.Optimized));
        var pts = r.Out.Sent.Where(s => s.E.Kind == EventKind.MouseMove).Select(s => (s.E.X, s.E.Y)).ToList();
        double biggest = 0;
        for (int k = 1; k < pts.Count; k++) biggest = Math.Max(biggest, Math.Sqrt(Math.Pow(pts[k].X - pts[k - 1].X, 2) + Math.Pow(pts[k].Y - pts[k - 1].Y, 2)));
        Assert.True(biggest < 60, $"largest single step {biggest:0}px: the path must stay continuous");
        Assert.True(r.Out.Sent.First(s => s.E.Kind == EventKind.ButtonDown).E.Y > 600, "ends on the target's new place");
    }

    // ---- continuing after a relocated click ----

    [Fact]
    public void After_a_click_on_a_moved_target_the_recorded_path_continues_from_the_click_without_a_jump_and_fades_back_to_the_recording()
    {
        var ev = new List<MacroEvent> { new(0, EventKind.MouseMove, X: 100, Y: 100), new(40, EventKind.MouseMove, X: 300, Y: 200),
            new(80, EventKind.TargetClick, Target: "t", Delta: 3000, Button: MouseBtn.Left, Step: new StepOptions { Move = MoveMode.FollowPath, FallbackX = 500, FallbackY = 300, RequireStable = false, Retries = 0, MinRepeatMs = 0 }) };
        var after = new[] { (510, 310), (560, 360), (700, 500), (1000, 700), (1500, 1000), (1900, 1050) };
        for (int k = 0; k < after.Length; k++) ev.Add(new MacroEvent(200 + k * 40, EventKind.MouseMove, X: after[k].Item1, Y: after[k].Item2));
        var all = ev.ToArray(); var snapshot = all.ToArray();
        var clock = new FakeClock(); var rec = new RecordingSink(clock); var log = new ExecutionLog();
        var ex = new StepExecutor(rec, new Fin { Center = () => (2500, 600) }, clock, () => false, log, () => 3, () => true, null, null, () => (100, 100));   // the target now sits far to the right (another monitor)
        int idx = 0;
        var sink = new TrajectorySink(ex, all, TrajectoryPlan.Build(all), new Fin { Center = () => (2500, 600) }, () => idx, p => p, () => true, log);
        for (idx = 0; idx < all.Length; idx++) sink.Send(all[idx]);

        var click = rec.Sent.First(s => s.E.Kind == EventKind.ButtonDown).E;
        Assert.Equal((2500, 600), (click.X, click.Y));
        var later = rec.Sent.SkipWhile(s => s.E.Kind != EventKind.ButtonUp).Skip(1).Where(s => s.E.Kind == EventKind.MouseMove).Select(s => (s.E.X, s.E.Y)).ToList();
        Assert.Equal(after.Length, later.Count);
        double Dist((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        Assert.True(Dist((2500, 600), later[0]) < 20, $"first movement after the click is {Dist((2500, 600), later[0]):0} px from the click point");
        for (int k = 1; k < later.Count; k++) Assert.True(Dist(later[k - 1], later[k]) < 1.2 * Dist(after[k - 1], after[k]) + 30, "no sudden jump between consecutive moves");
        Assert.True(Dist(later[^1], after[^1]) < 0.7 * Dist(later[0], after[0]), "the shift fades out along the recorded path");
        Assert.Equal(snapshot, all);
    }

    [Fact]
    public void The_pointer_is_corrected_when_it_lands_away_from_where_it_was_sent()
    {
        // a pointer that always lands (-7, +4) off, as a wrong monitor scale or offset would do
        var clock = new FakeClock(); var rec = new RecordingSink(clock);
        (int X, int Y) pos = (0, 0);
        var mid = new Hook(rec, e => { if (e.HasPos()) pos = (e.X - 7, e.Y + 4); });
        var ex = new StepExecutor(mid, new Fin { Center = () => (2200, 400) }, clock, () => false, new ExecutionLog(), () => 3, () => true, null, null, () => pos, correctCursor: true);
        ex.Send(Click(MoveMode.Optimized));
        var down = rec.Sent.First(s => s.E.Kind == EventKind.ButtonDown).E;
        Assert.InRange(down.X + (-7), 2199, 2201);                                   // injected position + the error = the target
        Assert.InRange(down.Y + 4, 399, 401);
        Assert.Equal((2200, 400), ex.LastClick);
    }

    sealed class Hook(IInputSink inner, Action<MacroEvent> seen) : IInputSink { public void Send(in MacroEvent e) { seen(e); inner.Send(in e); } }
}

public class TailTests
{
    [Fact]
    public void After_the_last_optimized_click_the_recorded_tail_is_not_replayed()
    {
        var ev = new List<MacroEvent> { new(0, EventKind.TargetClick, Target: "t", Step: new StepOptions { Move = MoveMode.Optimized }) };
        for (int k = 0; k < 30; k++) ev.Add(new MacroEvent(100 + k * 20, EventKind.MouseMove, X: (int)(600 + 200 * Math.Cos(k / 5.0)), Y: (int)(500 + 200 * Math.Sin(k / 5.0))));
        var all = ev.ToArray();
        var got = new List<MacroEvent>();
        int idx = 0;
        var sink = new TrajectorySink(new Cap(got), all, TrajectoryPlan.Build(all), new NoFinder(), () => idx, p => p, () => true);
        for (idx = 0; idx < all.Length; idx++) sink.Send(all[idx]);
        Assert.DoesNotContain(got, e => e.Kind == EventKind.MouseMove);
    }
    sealed class Cap(List<MacroEvent> l) : IInputSink { public void Send(in MacroEvent e) => l.Add(e); }
    sealed class NoFinder : ITargetFinder { public string? NameOf(string id) => "t"; public double ThresholdOf(string id) => 0.85; public Sighting? Find(string id) => null; }
}
