using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Playback;
using Xunit.Abstractions;

namespace MacroStudio.Tests;

/// <summary>Real clock, production chain (Player -> TrajectorySink -> StepExecutor -> sink): does a plain recording play at its recorded times?</summary>
[Collection("real desktop")]
public class PlaybackFidelityTests(ITestOutputHelper o)
{
    sealed class Stamp : IInputSink
    {
        public readonly List<double> At = new();
        public void Send(in MacroEvent e) => At.Add(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
    }
    sealed class NoTargets : ITargetFinder
    {
        public string? NameOf(string id) => null; public double ThresholdOf(string id) => 0.8; public Sighting? Find(string id) => null;
    }

    static MacroEvent[] Recording()
    {
        var l = new List<MacroEvent>(); double t = 0;
        for (int i = 0; i < 1500; i++)
        {
            t += 4 + (i % 3);                                                    // 4-6 ms mouse sampling
            l.Add(new MacroEvent(t, EventKind.MouseMove, X: 100 + i % 900, Y: 200 + i % 400));
            if (i % 50 == 10) { t += 1; l.Add(new MacroEvent(t, EventKind.KeyDown, Vk: 0x20)); t += 35; l.Add(new MacroEvent(t, EventKind.KeyUp, Vk: 0x20)); }
            if (i % 300 == 299) { t += 1; l.Add(new MacroEvent(t, EventKind.ButtonDown, X: 500, Y: 500)); t += 60; l.Add(new MacroEvent(t, EventKind.ButtonUp, X: 500, Y: 500)); }
        }
        return l.ToArray();
    }

    (double meanLate, double maxLate, double endDrift) Run(bool withFlow)
    {
        var ev = Recording(); var stamp = new Stamp(); Player? p = null;
        var exec = new StepExecutor(stamp, new NoTargets(), new RealClock(), () => p?.StopRequested == true, null, () => p?.CurrentIndex ?? -1);
        var chain = new TrajectorySink(exec, ev, TrajectoryPlan.Build(ev), new NoTargets(), () => p?.CurrentIndex ?? -1, q => q, () => true);
        p = new Player(chain);
        if (withFlow) { var flow = new FlowRunner(new NoTargets(), new RealClock(), () => p.StopRequested, exec, _ => null, (_, _) => (chain, exec)); p.Flow = flow; exec.Idle = () => flow.Poll(); }
        p.Run(ev, 0, ev.Length, 1);
        double start = stamp.At[0] - ev[0].T;
        var late = ev.Select((e, i) => stamp.At[i] - start - e.T).ToArray();
        return (late.Average(), late.Max(), late[^1]);
    }

    [Fact]
    public void Plain_recording_plays_at_its_recorded_times()
    {
        Run(false);   // warm up the JIT
        var a = Run(false); var b = Run(true);
        o.WriteLine($"no flow : mean late {a.meanLate:0.00} ms, max {a.maxLate:0.00}, end drift {a.endDrift:0.00}");
        o.WriteLine($"with flow: mean late {b.meanLate:0.00} ms, max {b.maxLate:0.00}, end drift {b.endDrift:0.00}");
        Assert.True(Math.Abs(b.endDrift) < 3, $"flow chain: max {b.maxLate:0.0} ms, drift {b.endDrift:0.0} ms");
        Assert.True(Math.Abs(a.endDrift) < 3, $"plain chain: max {a.maxLate:0.0} ms, drift {a.endDrift:0.0} ms");
    }

    [Fact]
    public void A_target_step_keeps_the_recorded_gap_after_it_and_only_shifts_what_it_overran()
    {
        var clock = new CoreTests.FakeClock(); var sink = new CoreTests.RecordingSink(clock);
        var ev = new[]
        {
            new MacroEvent(0, EventKind.MouseMove, X: 1, Y: 1), new MacroEvent(100, EventKind.TargetClick, Target: "t"),
            new MacroEvent(300, EventKind.MouseMove, X: 2, Y: 2), new MacroEvent(320, EventKind.TargetClick, Target: "t"), new MacroEvent(350, EventKind.MouseMove, X: 3, Y: 3),
        };
        int clicks = 0;
        sink.OnSend = n => { if (sink.Sent[n - 1].E.Kind == EventKind.TargetClick) clock.Now += clicks++ == 0 ? 50 : 200; };   // 1st click overruns by 50 ms, 2nd by 200 ms
        new Player(sink, clock).Run(ev, 0, ev.Length, 1);
        Assert.InRange(sink.Sent[2].At, 299, 301);     // 50 ms overrun fits in the 200 ms gap: the next move stays on its recorded time (was: fired at 150)
        Assert.InRange(sink.Sent[4].At - sink.Sent[3].At, 199, 201);   // 200 ms overrun swallows the 30 ms gap: the next move fires as the step ends, no burst, no extra delay
    }
}
