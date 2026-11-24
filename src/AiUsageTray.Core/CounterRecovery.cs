namespace AiUsageTray.Core;

/// <summary>
/// Classifies one step of a cumulative counter inside a single period. A reading that falls to exactly 0 is how the API
/// has been seen to report a glitch: the next readings return to the earlier value. Counting that climb back as consumption
/// multiplies the real usage, so the climb up to the earlier peak is an adjustment and only the excess is consumption.
/// A drop that is not followed by a return within <see cref="Window"/> is treated as a genuine reset.
/// </summary>
public static class CounterRecovery
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(6);

    public readonly record struct Result(DeltaKind Kind, decimal? Amount, decimal? Peak, DateTimeOffset? Since);

    public static Result Step(decimal before, decimal now, DateTimeOffset at, decimal? peak, DateTimeOffset? since)
    {
        if (peak is not null && since is { } opened && at - opened > Window)
        {
            // The earlier value never came back: a genuine reset. Everything counted since the drop is new usage.
            peak = null;
            before = 0;
        }
        if (peak is { } target)
        {
            if (now > target) return new(DeltaKind.Consumption, now - target, null, null);
            if (now == target) return new(DeltaKind.Adjustment, null, null, null);
            if (now == before) return new(DeltaKind.Consumption, 0, target, since);
            return new(DeltaKind.Adjustment, null, target, since);
        }
        if (now == 0 && before > 0) return new(DeltaKind.Adjustment, null, before, at);
        return now < before ? new(DeltaKind.Adjustment, null, null, null) : new(DeltaKind.Consumption, now - before, null, null);
    }
}
