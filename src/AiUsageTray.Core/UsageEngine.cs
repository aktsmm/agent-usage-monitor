using System.Globalization;
using System.Numerics;

namespace AiUsageTray.Core;

public static class UsageEngine
{
    public static IReadOnlyList<NotificationCandidate> Apply(
        UsageState state, FetchResult result, DateTimeOffset now)
    {
        state.Validate();
        foreach (var snapshot in result.Snapshots)
            snapshot.Validate();
        if (result.Snapshots.Select(s => s.Key).Distinct().Count() != result.Snapshots.Count)
            throw new UsageException(FailureKind.Schema, "Duplicate quota buckets.");
        state.Failures.AddRange(result.Failures);
        var candidates = new List<NotificationCandidate>();
        foreach (var current in result.Snapshots)
        {
            if (!current.IsFresh(now, state.Polling.MaximumAge))
                continue;
            var previous = state.Latest.FirstOrDefault(s => s.Key == current.Key);
            var quotaRule = state.QuotaRules.FirstOrDefault(r => r.Key == current.Key);
            var rules = quotaRule?.Rules ?? state.Rules;
            rules.Validate();
            if (previous is not null && previous.ObservedAt >= current.ObservedAt)
                continue;
            bool comparable = previous is not null && previous.PeriodId == current.PeriodId;
            bool reset = previous is not null &&
                previous.Window.ResetsAt is { } oldEnd && oldEnd <= current.ObservedAt &&
                current.Window.ResetsAt is { } newEnd && newEnd > oldEnd &&
                current.Window.StartsAt is { } newStart && newStart >= oldEnd &&
                previous.Plan == current.Plan && previous.Unit == current.Unit && previous.Total == current.Total &&
                previous.Window.Kind == current.Window.Kind;
            var kind = previous is null ? DeltaKind.Baseline
                : !comparable ? (reset ? DeltaKind.Reset : DeltaKind.DefinitionChanged)
                : !current.HasMeasurement || !previous.HasMeasurement ? DeltaKind.Adjustment
                : current.Used < previous.Used ? DeltaKind.Adjustment : DeltaKind.Consumption;
            decimal? amount = kind == DeltaKind.Consumption ? current.Used - previous!.Used : null;
            var recovery = state.Recoveries.FirstOrDefault(r => r.Key == current.Key && r.PeriodId == current.PeriodId);
            if (recovery is not null && !comparable)
            {
                state.Recoveries.Remove(recovery);
                recovery = null;
            }
            if (comparable && current.HasMeasurement && previous!.HasMeasurement &&
                previous.Used is { } before && current.Used is { } after)
            {
                var step = CounterRecovery.Step(before, after, current.ObservedAt, recovery?.Peak, recovery?.Since);
                (kind, amount) = (step.Kind, step.Amount);
                if (recovery is not null) state.Recoveries.Remove(recovery);
                if (step.Peak is { } peak)
                    state.Recoveries.Add(new(current.Key, current.PeriodId, peak, step.Since!.Value));
            }
            state.Deltas.Add(new(current.Key, current.PeriodId,
                previous?.ObservedAt ?? current.ObservedAt, current.ObservedAt, current.Unit, amount, kind));
            if (!comparable)
                state.Baselines.Add(current);
            state.Latest.RemoveAll(s => s.Key == current.Key);
            state.Latest.Add(current);
            if (quotaRule is { Enabled: false })
                continue;
            if (reset && rules.Reset)
                Add(state, candidates, current, "reset", NotificationReason.Reset, ["reset"], [], now);
            if (kind is DeltaKind.Adjustment or DeltaKind.DefinitionChanged || !current.HasMeasurement ||
                current.Window.ResetsAt is null)
                continue;
            if (current.Unlimited)
            {
                // No ceiling: percentages, thresholds and guidance do not apply; notify every AmountStep reported credits.
                if (rules.AmountStep is { } amountStep && current.Used is { } amountUsed)
                {
                    if (TryAmountLevels(amountUsed, amountStep, out var levels, out var first))
                        AddAmount(state, candidates, current, amountStep, levels, first, now);
                }
                continue;
            }
            if (current.UsedPercent is { } percent)
            {
                if (rules.PercentageStep is { } step)
                {
                    var levels = Enumerable.Range(1, (int)decimal.Floor(100 / step))
                        .Select(i => i * step).Where(x => x <= percent).ToArray();
                    Add(state, candidates, current, $"step:{Number(step)}",
                        NotificationReason.PercentageStep, levels.Select(Number).ToArray(), levels, now);
                }
                var thresholds = rules.Thresholds.Order().ToArray();
                var crossed = thresholds.Where(x => x <= percent).ToArray();
                Add(state, candidates, current, $"threshold:{string.Join(",", thresholds.Select(Number))}",
                    NotificationReason.Threshold, crossed.Select(Number).ToArray(), crossed, now);
            }
            foreach (bool weekly in new[] { false, true })
            {
                if (weekly ? !rules.WeeklyGuidance : !rules.DailyGuidance)
                    continue;
                var summary = Budgets.Summarize(state, current, weekly);
                if (summary.Measured is { } used && summary.Guidance is > 0 && used > summary.Guidance)
                    Add(state, candidates, current, weekly ? "weekly" : "daily",
                        weekly ? NotificationReason.WeeklyGuidance : NotificationReason.DailyGuidance,
                        [summary.StartsAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)],
                        [], now, summary.Coverage);
            }
        }
        return candidates;
    }

    public static bool AmountNotificationsUnavailable(UsageState state, QuotaSnapshot snapshot)
    {
        var quotaRule = state.QuotaRules.FirstOrDefault(r => r.Key == snapshot.Key);
        var rules = quotaRule?.Rules ?? state.Rules;
        return quotaRule is not { Enabled: false } && snapshot.Unlimited && snapshot.HasMeasurement &&
            snapshot.Window.ResetsAt is not null && snapshot.Used is { } amount &&
            rules.AmountStep is { } step && !TryAmountLevels(amount, step, out _, out _);
    }

    private static (BigInteger coefficient, int scale) Parts(decimal value)
    {
        var bits = decimal.GetBits(value);
        return ((BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 |
            (BigInteger)(uint)bits[2] << 64, (bits[3] >> 16) & 0x7f);
    }

    private static BigInteger MilestoneIndex(decimal amount, decimal step, out BigInteger remainder)
    {
        var (amountCoefficient, amountScale) = Parts(amount);
        var (stepCoefficient, stepScale) = Parts(step);
        return BigInteger.DivRem(amountCoefficient * BigInteger.Pow(10, stepScale),
            stepCoefficient * BigInteger.Pow(10, amountScale), out remainder);
    }

    private static bool TryAmountLevels(decimal amount, decimal step, out decimal[] levels, out BigInteger first)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        var (stepCoefficient, stepScale) = Parts(step);
        var reached = MilestoneIndex(amount, step, out _);
        var count = (int)BigInteger.Min(reached, (int)NotificationRules.MaximumAmountLevels);
        first = reached - count + 1;
        var maxCoefficient = (BigInteger.One << 96) - 1;
        var result = new decimal[count];
        for (int i = 0; i < count; i++)
        {
            var coefficient = (first + i) * stepCoefficient;
            var scale = stepScale;
            while (coefficient > maxCoefficient && scale > 0 && coefficient % 10 == 0)
            {
                coefficient /= 10;
                scale--;
            }
            if (coefficient > maxCoefficient)
            {
                levels = [];
                return false;
            }
            result[i] = new decimal(unchecked((int)(uint)(coefficient & uint.MaxValue)),
                unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue)),
                unchecked((int)(uint)((coefficient >> 64) & uint.MaxValue)), false, (byte)scale);
        }
        levels = result;
        return true;
    }

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static void AddAmount(UsageState state, List<NotificationCandidate> candidates,
        QuotaSnapshot snapshot, decimal step, decimal[] levels, BigInteger first, DateTimeOffset now)
    {
        if (levels.Length == 0) return;
        var rule = $"amount:{Number(step)}";
        var prefix = $"{snapshot.Key.Account.Provider}:{snapshot.Key.Account.Id}:{snapshot.Key.Bucket}:{snapshot.PeriodId}:{rule}:";
        var recorded = state.Notifications.SelectMany(n => n.DeduplicationKeys).ToHashSet();
        var matches = state.Notifications.Where(n => n.Key == snapshot.Key && n.PeriodId == snapshot.PeriodId &&
            n.RuleId == rule && n.Reason == NotificationReason.AmountStep).ToList();
        var ranges = new List<(BigInteger First, BigInteger Last)>();
        var compacted = new List<NotificationCandidate>();
        foreach (var notification in matches)
        {
            if (notification.AmountMilestoneRanges is { } storedRanges)
            {
                compacted.Add(notification);
                ranges.AddRange(storedRanges.Select(r => (BigInteger.Parse(r.First, CultureInfo.InvariantCulture),
                    BigInteger.Parse(r.Last, CultureInfo.InvariantCulture))));
            }
            else if (notification.CrossedLevels.Length > 0 &&
                notification.DeduplicationKeys.SequenceEqual(notification.CrossedLevels.Select(x => prefix + Number(x))))
            {
                var legacyIndices = notification.CrossedLevels.Select(x =>
                {
                    var index = MilestoneIndex(x, step, out var remainder);
                    return (index, remainder);
                }).ToArray();
                if (legacyIndices.All(x => x.index > 0 && x.remainder.IsZero))
                {
                    compacted.Add(notification);
                    ranges.AddRange(legacyIndices.Select(x => (x.index, x.index)));
                }
            }
        }

        var merged = MergeRanges(ranges);
        var pending = levels.Select((level, i) => (level, index: first + i))
            .Where(x => !recorded.Contains(prefix + Number(x.level)) &&
                !ContainsMilestone(merged, x.index)).ToArray();
        NotificationCandidate? candidate = null;
        if (pending.Length > 0)
        {
            candidate = new NotificationCandidate(Guid.NewGuid().ToString("N"), snapshot.Key, snapshot.PeriodId,
                rule, NotificationReason.AmountStep, now, pending.Select(x => x.level).ToArray(),
                pending.Select(x => prefix + Number(x.level)).ToArray());
            candidates.Add(candidate);
            merged.AddRange(pending.Select(x => (x.index, x.index)));
        }
        if (candidate is null && compacted.All(n => n.AmountMilestoneRanges is not null)) return;
        if (compacted.Count == 0 && candidate is null) return;

        merged = MergeRanges(merged);
        var createdAt = compacted.Select(n => n.CreatedAt).Append(candidate?.CreatedAt ?? DateTimeOffset.MinValue).Max();
        var saved = (candidate ?? compacted[0]) with
        {
            CreatedAt = createdAt,
            CrossedLevels = [],
            DeduplicationKeys = [],
            AmountMilestoneRanges = merged.Select(r => new AmountMilestoneRange(
                r.First.ToString(CultureInfo.InvariantCulture), r.Last.ToString(CultureInfo.InvariantCulture))).ToArray()
        };
        foreach (var notification in compacted) state.Notifications.Remove(notification);
        state.Notifications.Add(saved);
    }

    private static List<(BigInteger First, BigInteger Last)> MergeRanges(
        IEnumerable<(BigInteger First, BigInteger Last)> ranges)
    {
        var merged = new List<(BigInteger First, BigInteger Last)>();
        foreach (var range in ranges.OrderBy(r => r.First).ThenBy(r => r.Last))
        {
            if (merged.Count == 0 || range.First > merged[^1].Last + 1) merged.Add(range);
            else merged[^1] = (merged[^1].First, BigInteger.Max(merged[^1].Last, range.Last));
        }
        return merged;
    }

    private static bool ContainsMilestone(List<(BigInteger First, BigInteger Last)> ranges, BigInteger index)
    {
        int low = 0, high = ranges.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var range = ranges[middle];
            if (index < range.First) high = middle - 1;
            else if (index > range.Last) low = middle + 1;
            else return true;
        }
        return false;
    }

    private static void Add(UsageState state, List<NotificationCandidate> candidates,
        QuotaSnapshot snapshot, string rule, NotificationReason reason,
        string[] milestones, decimal[] levels, DateTimeOffset now, Coverage? coverage = null)
    {
        var prefix = $"{snapshot.Key.Account.Provider}:{snapshot.Key.Account.Id}:{snapshot.Key.Bucket}:{snapshot.PeriodId}:{rule}:";
        var recorded = state.Notifications.SelectMany(n => n.DeduplicationKeys).ToHashSet();
        var pending = milestones.Select(m => prefix + m).Where(k => !recorded.Contains(k)).ToArray();
        if (pending.Length == 0)
            return;
        var pendingLevels = levels.Where(x => pending.Contains(prefix + Number(x))).ToArray();
        var candidate = new NotificationCandidate(Guid.NewGuid().ToString("N"), snapshot.Key,
            snapshot.PeriodId, rule, reason, now, pendingLevels, pending, coverage);
        state.Notifications.Add(candidate);
        candidates.Add(candidate);
    }
}
