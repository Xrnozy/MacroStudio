namespace MacroStudio.Model;

/// <summary>What to do when the target cannot be found (or the click does not have the expected effect) after all retries.</summary>
public enum FailAction { Stop, Skip, UseRecordedPosition }

/// <summary>
/// How the mouse reaches the target. Jump = the recorded path exactly as recorded, then an instant jump to the target (the old behaviour).
/// FollowPath = the recorded approach path, bent at playback so it ends on the target's current position (same shape and timing; unchanged when the target has not moved).
/// Optimized = the recorded approach moves are not replayed at all: the cursor goes straight from where it is to the target's current position
/// (duration from distance and target size, smooth start and stop, re-aimed continuously while it travels), so no unneeded movement is made.
/// Glide = a smooth straight-ish movement from wherever the cursor is to the target, ignoring the recorded path. Nothing is ever written back to the recording.
/// </summary>
public enum MoveMode { Jump, FollowPath, Glide, Optimized }

/// <summary>Check after a click that it really did something.</summary>
public enum VerifyMode { None, TargetGone, OtherTargetAppears }

/// <summary>
/// Settings of an adaptive step (TargetClick / WaitTarget / WaitGone). Everything has a safe default, so a bare step still works.
/// The click point is relative to the detected target: centre + (RelX × width, RelY × height) + the event's pixel offset.
/// </summary>
public sealed record StepOptions
{
    /// <summary>Click anchor as a fraction of the target size from its centre: (0,0) centre, (0.5,0.5) bottom-right corner, (-0.5,-0.5) top-left.</summary>
    public double RelX { get; init; }
    public double RelY { get; init; }

    /// <summary>Lowest acceptable detection score. 0 = the target's own threshold.</summary>
    public double MinConfidence { get; init; }

    /// <summary>Extra attempts after the first one fails (target not found, or verification failed).</summary>
    public int Retries { get; init; } = 2;
    public int RetryDelayMs { get; init; } = 300;

    /// <summary>Click only when two separate detections agree on the position (within 3 px). Stops clicks on a flickering false match.</summary>
    public bool RequireStable { get; init; } = true;

    /// <summary>Minimum time between two clicks on the same spot of the same target. Stops accidental double clicks from unstable detection.</summary>
    public int MinRepeatMs { get; init; } = 250;

    /// <summary>How the mouse travels to the target. Default FollowPath: the recorded movement, played at its recorded times and bent to where the target is now. Optimized (straight to the target) must be chosen explicitly.</summary>
    public MoveMode Move { get; init; } = MoveMode.FollowPath;
    /// <summary>Duration of a glide in milliseconds at normal speed; 0 = automatic from the distance.</summary>
    public int GlideMs { get; init; }

    public FailAction OnFail { get; init; } = FailAction.Stop;
    /// <summary>The click position as recorded, in screen pixels. Used only for <see cref="FailAction.UseRecordedPosition"/> and only when the screen layout is unchanged.</summary>
    public int FallbackX { get; init; }
    public int FallbackY { get; init; }

    /// <summary>A Discord message to send right after this click succeeds (empty = none). The webhook itself is never stored here.</summary>
    public string? DiscordText { get; init; }
    /// <summary>Attach a screenshot to that message.</summary>
    public bool DiscordShot { get; init; }

    public VerifyMode Verify { get; init; }
    /// <summary>For <see cref="VerifyMode.OtherTargetAppears"/>: the target that must become visible.</summary>
    public string? VerifyTarget { get; init; }
    public int VerifyTimeoutMs { get; init; } = 2000;

    /// <summary>When verification fails: press this key (virtual-key code, 0 = none) and/or click this target (e.g. Esc, or a "Close" button), then retry.</summary>
    public int RecoveryKey { get; init; }
    public string? RecoveryTarget { get; init; }

    /// <summary>Options equivalent to a step created before adaptive options existed: one attempt, no stability check.</summary>
    public static StepOptions Legacy(bool skipIfMissing) => new() { OnFail = skipIfMissing ? FailAction.Skip : FailAction.Stop, Retries = 0, RequireStable = false };

    /// <summary>"61% / 83%" style text for the anchor, measured from the top-left corner of the target.</summary>
    public string AnchorText => $"{(0.5 + RelX) * 100:0}% / {(0.5 + RelY) * 100:0}%";
}
