namespace AiUsageTray.Core;

public static class HistoryRetention
{
    public const int Days = 90;

    public static void Prune(UsageState state, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-Days);
        state.Deltas.RemoveAll(d => d.To < cutoff);
        state.Failures.RemoveAll(f => f.AttemptedAt < cutoff);
        var active = state.Latest.Select(s => (s.Key, s.PeriodId)).ToHashSet();
        state.Notifications.RemoveAll(n => n.CreatedAt < cutoff && !active.Contains((n.Key, n.PeriodId)));
        var referenced = active.Concat(state.Deltas.Select(d => (d.Key, d.PeriodId))).ToHashSet();
        state.Baselines = state.Baselines.Where(b => referenced.Contains((b.Key, b.PeriodId)))
            .GroupBy(b => (b.Key, b.PeriodId)).Select(g => g.MinBy(b => b.ObservedAt)!).ToList();
    }
}
