namespace AiUsageTray.Core;

public static class JapaneseCalendar
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(9);

    public static DateTimeOffset DayStart(DateTimeOffset at) =>
        new(at.ToOffset(Offset).Date, Offset);

    public static DateOnly Date(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(Offset).Date);

    public static DateTimeOffset WeekStart(DateTimeOffset at)
    {
        var day = DayStart(at);
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }
}

public static class Budgets
{
    public static Guidance? Calculate(QuotaSnapshot snapshot)
    {
        if (snapshot.Unlimited || snapshot.Total is not > 0 ||
            snapshot.Window.StartsAt is not { } start || snapshot.Window.ResetsAt is not { } end)
            return null;

        decimal days = snapshot.Window.Kind switch
        {
            WindowKind.Weekly when end - start == TimeSpan.FromDays(7) => 7,
            WindowKind.Monthly => (decimal)(end - start).TotalDays,
            _ => 0
        };
        if (days <= 0)
            return null;
        var daily = snapshot.Total.Value / days;
        return new(daily, daily * 7, snapshot.Unit, snapshot.PeriodId);
    }

    public static ConsumptionSummary Summarize(UsageState state, QuotaSnapshot snapshot, bool weekly)
    {
        var calendarStart = weekly ? JapaneseCalendar.WeekStart(snapshot.ObservedAt)
            : JapaneseCalendar.DayStart(snapshot.ObservedAt);
        var calendarEnd = calendarStart.AddDays(weekly ? 7 : 1);
        var start = snapshot.Window.StartsAt is { } providerStart && providerStart > calendarStart
            ? providerStart : calendarStart;
        var end = snapshot.Window.ResetsAt is { } providerEnd && providerEnd < calendarEnd
            ? providerEnd : calendarEnd;
        var deltas = state.Deltas.Where(d => d.Key == snapshot.Key &&
            d.PeriodId == snapshot.PeriodId && d.To >= start && d.From < end).ToArray();
        var measurable = deltas.Where(d => d.Kind == DeltaKind.Consumption &&
            d.From >= start && d.To <= end && d.Amount is not null).ToArray();
        var baseline = state.Baselines.FirstOrDefault(b => b.Key == snapshot.Key &&
            b.PeriodId == snapshot.PeriodId);
        bool missing = baseline is null || baseline.ObservedAt > start ||
            deltas.Any(d => d.Kind == DeltaKind.Baseline ? d.To > start
                : d.Kind != DeltaKind.Consumption || d.From < start || d.To > end);
        var coverage = measurable.Length == 0 ? Coverage.Unmeasured
            : missing ? Coverage.Partial : Coverage.CompleteToObservation;
        var guidance = Calculate(snapshot);
        // A calendar week crossing a provider reset is shown as separate quota-period slices.
        decimal? limit = guidance is null ? null
            : guidance.Daily * (decimal)(end - start).TotalDays;
        return new(measurable.Length == 0 ? null : measurable.Sum(d => d.Amount!.Value),
            limit, coverage, snapshot.Unit, start, end, snapshot.ObservedAt);
    }
}
