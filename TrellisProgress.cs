// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonThreeD

namespace Season.ThreeD;

/// <summary>
/// A progress tick from the worker. Stage banners advance <see cref="Stage"/>; the
/// sampler's per-step lines fill <see cref="Step"/>/<see cref="TotalSteps"/> for the
/// stage they belong to (sparse structure, shape SLAT or texture SLAT).
/// </summary>
public sealed class TrellisProgress
{
    internal TrellisProgress(
        TrellisStage stage,
        string message,
        int stageIndex,
        int stageCount,
        int step,
        int totalSteps,
        TimeSpan elapsed,
        TimeSpan? remaining,
        bool isCompleted)
    {
        Stage = stage;
        Message = message;
        StageIndex = stageIndex;
        StageCount = stageCount;
        Step = step;
        TotalSteps = totalSteps;
        Elapsed = elapsed;
        Remaining = remaining;
        IsCompleted = isCompleted;
    }

    /// <summary>Stable stage identifier.</summary>
    public TrellisStage Stage { get; }

    /// <summary>The raw line the worker printed, trimmed (banners and progress alike).</summary>
    public string Message { get; }

    /// <summary>Stage number as printed by the worker (<c>[3/6]</c> reports 3).</summary>
    public int StageIndex { get; }

    /// <summary>Stage total as printed by the worker (<c>[3/6]</c> reports 6).</summary>
    public int StageCount { get; }

    /// <summary>Completed sampler steps within the current stage; 0 outside flow stages.</summary>
    public int Step { get; }

    /// <summary>Sampler steps the current stage runs; 0 outside flow stages.</summary>
    public int TotalSteps { get; }

    /// <summary>Time spent in the current stage so far, as the worker reports it.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>The worker's own ETA for the current stage, when it had a rate.</summary>
    public TimeSpan? Remaining { get; }

    /// <summary>Whether the run printed its final "done in ..." line.</summary>
    public bool IsCompleted { get; }

    /// <summary>Step fraction within the current flow stage, or <see langword="null"/>.</summary>
    public double? Fraction => TotalSteps > 0 ? Math.Clamp((double)Step / TotalSteps, 0.0, 1.0) : null;

    internal static TrellisProgress FromUpdate(TrellisProgressUpdate update, TimeSpan wallClock)
        => new(
            update.Stage,
            update.Message,
            update.StageIndex,
            update.StageCount,
            update.Step,
            update.TotalSteps,
            update.ElapsedSeconds is double elapsed ? TimeSpan.FromSeconds(elapsed) : wallClock,
            update.RemainingSeconds is double remaining ? TimeSpan.FromSeconds(remaining) : null,
            update.IsCompleted);
}

/// <summary>Parser output, before the runner stamps its wall-clock time on it.</summary>
internal readonly record struct TrellisProgressUpdate(
    TrellisStage Stage,
    string Message,
    int StageIndex,
    int StageCount,
    int Step,
    int TotalSteps,
    double? ElapsedSeconds,
    double? RemainingSeconds,
    bool IsCompleted);
