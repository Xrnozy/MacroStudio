using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using MacroStudio.Model;
using MacroStudio.Monitoring;
using MacroStudio.Playback;
using MacroStudio.Views;
using MacroStudio.Vision;

namespace MacroStudio;

/// <summary>Debugging, monitoring and safety: run log, detections, step debugger, performance line, user/display guards, adaptive-click commands.</summary>
public partial class MainWindow
{
    readonly PerfMonitor _perf = new();
    UserActivityGuard? _userGuard;
    DisplaySnapshot? _displayAtStart;
    DateTime _lastSlow = DateTime.MinValue;
    long _logShown = -1;
    string? _detectSig;
    readonly Dictionary<string, ImageSource?> _thumbs = new();

    public sealed record Row(string Text, Brush Brush);

    static readonly Brush WarnBrush = Theme.Brush("#B45309"), FallbackBrush = Theme.Brush("#7C3AED"), OkBrush = Theme.Brush("#15803D");

    void InitDiagnostics()
    {
        const string template = "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Margin='2,1'>" +
            "<Ellipse Width='7' Height='7' Fill='{Binding Brush}' HorizontalAlignment='Left' VerticalAlignment='Top' Margin='0,5,0,0'/>" +
            "<TextBlock Text='{Binding Text}' Foreground='{Binding Brush}' FontFamily='Consolas' FontSize='11.5' Margin='16,0,0,0' TextWrapping='Wrap'/></Grid></DataTemplate>";
        LogList.ItemTemplate = (DataTemplate)XamlReader.Parse(template);
        DetectList.ItemTemplate = (DataTemplate)XamlReader.Parse(template);
    }

    /// <summary>Target picture for the inspector's anchor picker (cached).</summary>
    ImageSource? TargetThumb(string id)
    {
        if (_thumbs.TryGetValue(id, out var t)) return t;
        try { t = Theme.ToBitmap(_targets.Image(id)); } catch { t = null; }
        return _thumbs[id] = t;
    }

    // ---- once-a-second work: performance line, log and detections, guards ----

    void SlowTick()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSlow).TotalMilliseconds < 1000) return;
        _lastSlow = now;

        _perf.Sample(now);
        var st = _watcher.Stats;
        var warn = _perf.Warning();
        PerfText.Text = $"CPU {_perf.CpuPercent:0.0}%  ·  RAM {_perf.RamMb:0} MB (managed {_perf.ManagedMb:0})  ·  detection {st.Tracked} tracked / {st.Searching} searching, {st.WorkMs:0} ms per tick, {st.Duty:P0} of a core" +
                        $"  ·  playback lateness {_player?.MaxLatenessMs ?? 0:0.0} ms  ·  log {_log.Total:N0}" + (warn != null ? $"   ⚠ {warn}" : "");
        PerfText.Foreground = warn != null ? Theme.Warn : Theme.Muted;

        if (_playing) CheckDisplay();
        if (LogTab.IsSelected) RefreshLogTab();
        if (DetectTab.IsSelected) RefreshDetections();
        if (_playing) DebugText.Text = DebugInfo();
    }

    void RefreshLogTab()
    {
        if (_log.Total == _logShown) return;
        _logShown = _log.Total;
        var rows = _log.Snapshot(400).Select(x => new Row(
            $"{x.Utc.ToLocalTime():HH:mm:ss.fff}  #{(x.Index >= 0 ? (x.Index + 1).ToString() : "-"),-6} {x.Result.ToString().ToUpperInvariant(),-8} {x.What}: {x.Message}" +
            (x.Confidence is { } c ? $"  [confidence {c:0.00}]" : "") + (x.X is { } px ? $"  [at {px}, {x.Y}]" : "") + (x.ElapsedMs > 0 ? $"  [{x.ElapsedMs:0} ms]" : ""),
            x.Result switch { LogResult.Failed => Theme.Warn, LogResult.Warn or LogResult.Retry => WarnBrush, LogResult.Skipped => Theme.Muted, LogResult.Fallback => FallbackBrush, _ => Theme.Text })).ToList();
        LogList.ItemsSource = rows;
        if (rows.Count > 0) LogList.ScrollIntoView(rows[^1]);
    }

    void RefreshDetections()
    {
        var now = DateTime.UtcNow;
        var rows = new List<Row>();
        foreach (var t in MyTargets().OrderBy(t => t.Group).ThenBy(t => t.Name))
        {
            string label = string.IsNullOrWhiteSpace(t.Group) ? t.Name : $"[{t.Group}] {t.Name}";
            var s = _watcher.Get(t.Id);
            if (s == null) { rows.Add(new Row($"{label}: not being watched (turn on Live detect, or play a macro that uses it)", Theme.Muted)); continue; }
            if (s.Hit is { } h)
                rows.Add(new Row($"{label}: FOUND at ({h.Center.X}, {h.Center.Y})  confidence {h.Score:0.00}  {h.Method}{(s.Ambiguous ? "  ⚠ AMBIGUOUS: another match looks the same" : "")}{(h.Visible < 0.995 ? $"  {h.Visible:P0} visible" : "")}  seen {(now - s.SeenUtc).TotalSeconds:0.0} s ago  (threshold {t.Threshold:0.00})", OkBrush));
            else
                rows.Add(new Row($"{label}: not visible{(s.LostUtc is { } lost ? $" since {lost.ToLocalTime():HH:mm:ss}" : "")}  ({s.Note})", Theme.Warn));
            foreach (var item in _watcher.History(t.Id).TakeLast(4))
                rows.Add(new Row($"      {item.Utc.ToLocalTime():HH:mm:ss}  " + (item.Lost ? $"lost (was at {item.Rect.X + item.Rect.Width / 2}, {item.Rect.Y + item.Rect.Height / 2})" : $"at ({item.Rect.X + item.Rect.Width / 2}, {item.Rect.Y + item.Rect.Height / 2})  {item.Score:0.00}  {item.Method}"), Theme.Muted));
        }
        if (rows.Count == 0) rows.Add(new Row("No visual targets registered yet. Use ＋ Capture in the Visual targets panel.", Theme.Muted));
        var sig = string.Join("|", rows.Select(r => r.Text));
        if (sig == _detectSig) return;   // unchanged: do not rebuild the list (it would reset scroll and hover once a second)
        _detectSig = sig;
        DetectList.ItemsSource = rows;
    }

    /// <summary>The step debugger: what is about to run, what it is looking for, what the engine sees, where it will click.</summary>
    string DebugInfo()
    {
        if (_player == null || _playEvents.Length == 0) return "";
        int i = Math.Clamp(_player.CurrentIndex, 0, _playEvents.Length - 1);
        var e = _playEvents[i];
        var sb = new StringBuilder();
        sb.Append(_player.IsPaused ? "PAUSED before" : "NOW at").Append($" event {i + 1} of {_playEvents.Length}: {e.Describe()}");
        if (_s.IsBreakpoint(e.Id)) sb.Append("   ● breakpoint");
        if (e.IsTarget() && e.Target is { } id)
        {
            sb.Append($"\n  looking for: {EventInfo.TargetLabel(id)}");
            var st = _watcher.Get(id);
            if (st?.Hit is { } h)
            {
                sb.Append($"\n  detected at ({h.Center.X}, {h.Center.Y})  rect {h.Rect.Width}×{h.Rect.Height}  confidence {h.Score:0.00}  {h.Method}{(st.Ambiguous ? "  ⚠ ambiguous: another match looks the same" : "")}");
                if (e.Kind == EventKind.TargetClick)
                {
                    var o = e.Step ?? StepOptions.Legacy(e.Extended);
                    var p = ClickPoint.Compute(h, o, e.X, e.Y);
                    sb.Append($"\n  will click at ({p.X}, {p.Y})  (anchor {o.AnchorText}{(e.X != 0 || e.Y != 0 ? $", offset {e.X},{e.Y}" : "")})");
                    var mode = _dynamicPath ? o.Move : MoveMode.Jump;
                    sb.Append("\n  mouse movement: " + mode switch
                    {
                        MoveMode.FollowPath => "recorded path bent to the target's position, then a short glide if needed",
                        MoveMode.Glide => "smooth glide from the cursor to the target",
                        MoveMode.Optimized => "recorded approach skipped; most direct smooth movement from the cursor to the target, re-aimed while it moves",
                        _ => _dynamicPath ? "exact recorded path, then jump to the target" : "exact recorded path, then jump (global switch is off)",
                    });
                }
            }
            else sb.Append($"\n  not detected right now ({st?.Note ?? "not being watched"})");
            if (e.Kind == EventKind.TargetClick && e.Step is { OnFail: FailAction.UseRecordedPosition } fb)
                sb.Append($"\n  fallback: recorded position ({fb.FallbackX}, {fb.FallbackY}) — {(FallbackSafe() ? "safe, screen layout unchanged" : "NOT safe, screen layout changed")}");
        }
        else if (e.HasPos()) sb.Append($"\n  recorded position ({e.X}, {e.Y}){(AdaptBox.IsChecked == true ? " (remapped to this PC's screens when needed)" : "")}");
        var last = _log.Snapshot(1);
        if (last.Count > 0) sb.Append($"\n  last result: {last[0].Result} — {last[0].What}: {last[0].Message}");
        return sb.ToString();
    }

    // ---- safety ----

    void CheckDisplay()
    {
        if (_displayAtStart == null || _player == null) return;
        var cur = DisplaySnapshot.Current();
        if (cur.DifferenceFrom(_displayAtStart) is not { } diff) return;
        _displayAtStart = cur;                     // report each change once; the user decides whether to carry on
        if (!_player.IsPaused) _player.Pause();
        _log.Add(-1, "safety", LogResult.Warn, $"paused: {diff}");
        SetStatus($"⏸ Paused: {diff}. Check the screen, then resume with {_keys.Name("pause")} (or stop with {_keys.Name("stop")}).");
    }

    void StartGuards()
    {
        _displayAtStart = DisplaySnapshot.Current();
        if (PauseOnUserBox.IsChecked != true) return;
        try { _userGuard = new UserActivityGuard(() => Dispatcher.BeginInvoke(OnUserTookOver), _keys.KeysToIgnore(), OverOwnWindow); }
        catch (Exception ex) { _log.Add(-1, "safety", LogResult.Warn, "could not watch for user input: " + ex.Message); }
    }

    /// <summary>The pointer is over this app's own window while it is kept visible: the user watching or scrolling it is not taking over.</summary>
    bool OverOwnWindow(MacroStudio.Native.RawInput r)
    {
        if (!(_visiblePlay || _recVisible)) return false;
        if (r.Kind is MacroStudio.Model.EventKind.KeyDown or MacroStudio.Model.EventKind.KeyUp) return false; // keys carry no position (0,0)
        var b = Views.WindowPlacement.Bounds(_hwnd);
        return r.X >= b.L && r.X < b.R && r.Y >= b.T && r.Y < b.B;
    }

    // ---- keep the window out of the way of the paths ----

    /// <summary>Everything the macro uses on screen: the recorded path, where the pointer has actually been, and where the targets are right now.</summary>
    List<(int X, int Y)> BusyNow()
    {
        var busy = WindowPlacer.Along(_recordedPath);
        if (_trace != null) busy.AddRange(WindowPlacer.Along(_trace.Snapshot().TakeLast(1500)));
        if (_finder != null)
            foreach (var id in _playEvents.Where(e => e.IsTarget() && e.Target != null).Select(e => e.Target!).Distinct())
                if (_finder.Find(id) is { } s) busy.AddRange(WindowPlacer.Fill(new ScreenRect(s.Rect.X, s.Rect.Y, s.Rect.Width, s.Rect.Height)));
        return busy;
    }

    void MakeRoomForPlayback()
    {
        _savedState = WindowState;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        if (WindowState == WindowState.Minimized) return;
        _savedBounds = Views.WindowPlacement.Bounds(_hwnd);
        var b = _savedBounds.Value;
        var (r, _) = WindowPlacer.Choose(MacroStudio.Native.Screens.Current().Monitors, BusyNow(), b.R - b.L, b.B - b.T, _compact ? b.R - b.L : 480, _compact ? b.B - b.T : 340);
        Views.WindowPlacement.MoveResize(_hwnd, r);
        _lastPlaced = Environment.TickCount64;
    }

    /// <summary>Re-checked while playing: targets move and the pointer's real path becomes known. Moves only when it clearly helps.</summary>
    void AvoidPaths()
    {
        _lastPlaced = Environment.TickCount64;
        var b = Views.WindowPlacement.Bounds(_hwnd);
        var busy = BusyNow();
        var cur = new ScreenRect(b.L, b.T, b.R - b.L, b.B - b.T);
        int here = WindowPlacer.Cost(cur, busy);
        if (here == 0) return;
        var (r, cost) = WindowPlacer.Choose(MacroStudio.Native.Screens.Current().Monitors, busy, cur.Width, cur.Height, _compact ? cur.Width : 480, _compact ? cur.Height : 340);
        if (cost * 2 <= here) Views.WindowPlacement.MoveResize(_hwnd, r);
    }

    void RestoreWindow()
    {
        if (_savedBounds is { } b) Views.WindowPlacement.MoveResize(_hwnd, new ScreenRect(b.L, b.T, b.R - b.L, b.B - b.T));
        _savedBounds = null;
        if (_savedState == WindowState.Maximized) WindowState = WindowState.Maximized;
    }

    void StopGuards()
    {
        _userGuard?.Dispose(); _userGuard = null;
        _displayAtStart = null;
    }

    void OnUserTookOver()
    {
        if (!_playing || _player == null || _player.IsPaused) return;
        _player.Pause();
        _log.Add(-1, "safety", LogResult.Warn, "paused: you used the mouse or keyboard");
        SetStatus($"⏸ Paused because you used the mouse or keyboard. Resume with {_keys.Name("pause")}, or stop with {_keys.Name("stop")}.");
    }

    // ---- adaptive click commands ----

    void ConvertClick_Click(object sender, RoutedEventArgs e)
    {
        if (_s.PrimaryEvent is not { } sel || !sel.IsButton()) { SetStatus("Select a recorded mouse click first (a button press or release)."); return; }
        int idx = _s.IndexOfId(sel.Id);
        var down = sel.Kind == EventKind.ButtonDown ? sel : idx >= 0 && _s.Analysis.Partner[idx] >= 0 ? _s.Events[_s.Analysis.Partner[idx]] : sel;
        var all = MyTargets().ToList();
        if (all.Count == 0) { SetStatus("Register a visual target first (＋ Capture in the Visual targets panel)."); return; }
        var id = Prompt.Choose(this, "Adaptive click", "Which visual target was this click on? Playback will click the same spot of that target wherever it is.",
            all.Select(t => (t.Id, string.IsNullOrWhiteSpace(t.Group) ? t.Name : $"{t.Group} / {t.Name}")).ToList());
        if (id == null) return;
        var target = all.First(t => t.Id == id);

        double rx = 0, ry = 0;
        string where = "the centre";
        if (target.CaptureRect.Width > 0)
        {
            (rx, ry) = ClickPoint.FromRecorded(target.CaptureRect, down.X, down.Y);
            if ((Math.Abs(rx) > 0.75 || Math.Abs(ry) > 0.75) &&
                MessageBox.Show(this, "This click is well outside the target as it was registered, so playback can only click the nearest point ON the target. Convert anyway? (If you meant another target, answer No and pick it.)",
                    "Macro Studio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            where = new StepOptions { RelX = rx, RelY = ry }.AnchorText + " of the target";
        }
        var step = new StepOptions { RelX = rx, RelY = ry, FallbackX = down.X, FallbackY = down.Y, Move = MoveMode.FollowPath };
        _s.ReplaceWith([down.Id], [new MacroEvent(down.T, EventKind.TargetClick, Button: down.Button, Delta: 10000, Target: id, Step: step)]);
        SetStatus($"Converted to an adaptive click on '{target.Name}' at {where}. In the inspector you can set retries, verification, and 'Click the recorded position' as a fallback.");
    }

    void Breakpoint_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Primary == 0) { SetStatus("Select an event first."); return; }
        _s.ToggleBreakpoint(_s.Primary);
        SetStatus(_s.IsBreakpoint(_s.Primary) ? "Breakpoint set: playback pauses before this event." : "Breakpoint removed.");
    }

    void InsertTarget_Opened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        var menu = (MenuItem)sender;
        var kind = Enum.Parse<EventKind>((string)menu.Tag);
        menu.Items.Clear();
        var all = MyTargets().OrderBy(t => t.Group).ThenBy(t => t.Name).ToList();
        if (all.Count == 0) { menu.Items.Add(new MenuItem { Header = "(capture a target first)", IsEnabled = false }); return; }
        foreach (var t in all)
        {
            var item = new MenuItem { Header = string.IsNullOrWhiteSpace(t.Group) ? t.Name : $"{t.Group} / {t.Name}" };
            var tid = t.Id;
            item.Click += (_, _) => _s.InsertBlock([new MacroEvent(0, kind, Delta: 10000, Target: tid, Step: kind == EventKind.TargetClick ? new StepOptions { Move = MoveMode.FollowPath } : null)], _s.PlayheadMs, ripple: true);
            menu.Items.Add(item);
        }
    }
}
