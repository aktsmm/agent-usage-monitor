namespace AiUsageTray.Core;

public static class HistoryRetention
{
    public const int Days = 90;

    public static void Prune(UsageState state, DateTimeOffset now,
        IReadOnlySet<string>? verifiedArchivedRecords = null)
    {
        var cutoff = now.AddDays(-Days);
        // Never discard the only raw copy, including during Load before export/migration.
        if (verifiedArchivedRecords is not null)
        {
            var exported = DailyHistory.Import(state).ToDictionary(r => r.Delta, r => r.Id);
            state.Deltas.RemoveAll(d => d.To < cutoff && verifiedArchivedRecords.Contains(exported[d]));
        }
        state.Failures.RemoveAll(f => f.AttemptedAt < cutoff);
        var active = state.Latest.Select(s => (s.Key, s.PeriodId)).ToHashSet();
        state.Notifications.RemoveAll(n => n.CreatedAt < cutoff && !active.Contains((n.Key, n.PeriodId)));
        var referenced = active.Concat(state.Deltas.Select(d => (d.Key, d.PeriodId))).ToHashSet();
        state.Baselines = state.Baselines.Where(b => referenced.Contains((b.Key, b.PeriodId)))
            .GroupBy(b => (b.Key, b.PeriodId)).Select(g => g.MinBy(b => b.ObservedAt)!).ToList();
    }
}
