using System.Globalization;

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
                current.Unlimited || current.Window.ResetsAt is null)
                continue;

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

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

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
