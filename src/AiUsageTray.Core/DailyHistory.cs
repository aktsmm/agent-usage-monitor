using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageTray.Core;

public sealed record HistoryRecord(int Version, string Id, QuotaSnapshot Snapshot, UsageDelta Delta)
{
    public static HistoryRecord Create(QuotaSnapshot snapshot, UsageDelta delta)
    {
        var record = new HistoryRecord(1, "", snapshot, delta);
        record = record with { Id = record.ContentId() };
        record.Validate();
        return record;
    }

    private string ContentId() => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Version, Delta }, StateStore.JsonOptions)));

    public void Validate()
    {
        if (Version != 1 || Snapshot is null || Delta is null)
            throw new UsageException(FailureKind.Storage, "history_record_version_or_shape");
        Snapshot.Validate();
        if (Snapshot.Key.Account.Id.Length != 64 || !Snapshot.Key.Account.Id.All(Uri.IsHexDigit) ||
            !Enum.IsDefined(Snapshot.Key.Account.Provider) || !Enum.IsDefined(Snapshot.Unit) ||
            !Enum.IsDefined(Snapshot.Source) || !Enum.IsDefined(Snapshot.Window.Kind) ||
            !Enum.IsDefined(Delta.Kind) || Snapshot.Key.Bucket.Length > 200 ||
            Snapshot.Key != Delta.Key || Snapshot.PeriodId != Delta.PeriodId || Snapshot.Unit != Delta.Unit ||
            Delta.From > Delta.To || Snapshot.ObservedAt != Delta.To || Delta.Amount < 0 ||
            Snapshot.Window.StartsAt > Snapshot.ObservedAt || Snapshot.Window.ResetsAt <= Snapshot.ObservedAt ||
            (Delta.Kind == DeltaKind.Consumption && (Delta.From == Delta.To || Delta.Unit == UsageUnit.Unknown)) ||
            (Delta.Kind == DeltaKind.Consumption ? Delta.Amount is null : Delta.Amount is not null) ||
            Id != ContentId())
            throw new UsageException(FailureKind.Storage, "history_record_invalid");
    }

    public IReadOnlyList<DateOnly> AffectedDays()
    {
        var to = JapaneseCalendar.Date(Delta.To);
        var from = JapaneseCalendar.Date(Delta.From);
        return Delta.Kind == DeltaKind.Consumption && from != to ? [from, to] : [to];
    }
}

public sealed record DailyHistorySlice(
    DateOnly Date, QuotaKey Key, string PeriodId, string? Plan, UsageUnit Unit, QuotaWindow Window,
    decimal? Observed, decimal? Guidance, Coverage Coverage,
    DateTimeOffset? FirstObservation, DateTimeOffset? ObservedThrough, DeltaKind[] Events,
    bool UnallocatedBoundary = false, bool HasGaps = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Cumulative { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CumulativeCaptured { get; init; }

    public decimal? GuidancePercent => Guidance is > 0 && Observed is { } value
        ? value / Guidance * 100 : null;
}

public sealed record HistoryQuery(DateOnly From, DateOnly Through, QuotaKey Key)
{
    public void Validate()
    {
        if (Key is null || Key.Account is null || From > Through ||
            Through.DayNumber - From.DayNumber >= 93)
            throw new UsageException(FailureKind.Schema, "history_query_range_or_key");
    }
}

public sealed record HistoryDay(DateOnly Date, IReadOnlyList<DailyHistorySlice> Slices)
{
    public Coverage Coverage => Slices.Count == 0 ? Coverage.Unmeasured
        : Slices.All(s => s.Coverage == Coverage.Unmeasured) ? Coverage.Unmeasured
        : Slices.All(s => s.Coverage == Coverage.CompleteToObservation)
            ? Coverage.CompleteToObservation : Coverage.Partial;
}

public sealed record HistoryOverviewItem(
    QuotaKey Key, string? PeriodId, string? Plan, UsageUnit Unit, QuotaWindow? Window,
    decimal? Observed, int MeasuredDays, int PartialDays, int UnmeasuredDays);

public sealed record HistoryOverviewPage(List<HistoryOverviewItem> Items, int? NextOffset, string Snapshot);

public sealed record HistoryDayPage(List<HistoryDay> Days, int? NextOffset, string Snapshot);

public static class DailyHistory
{
    public static string Month(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    internal static decimal? CumulativeAtEndOfDay(DateOnly date, QuotaKey key, string periodId,
        DateTimeOffset? observedThrough, IEnumerable<HistoryRecord> records)
    {
        if (observedThrough is null) return null;
        var candidates = records.Where(r => r.Snapshot.Key == key && r.Snapshot.PeriodId == periodId &&
            JapaneseCalendar.Date(r.Snapshot.ObservedAt) == date &&
            r.Snapshot.ObservedAt <= observedThrough).ToArray();
        if (candidates.Length == 0) return null;
        var latest = candidates.Max(r => r.Snapshot.ObservedAt);
        if (JapaneseCalendar.Date(observedThrough.Value) == date && latest != observedThrough) return null;
        var values = candidates.Where(r => r.Snapshot.ObservedAt == latest)
            .Select(r => r.Snapshot.Used).Distinct().ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    public static bool IsPartial(DateOnly date, DailyHistorySlice? slice, DateTimeOffset now)
    {
        if (slice?.Coverage != Coverage.CompleteToObservation) return true;
        if (date > JapaneseCalendar.Date(now)) return false;
        if (date == DateOnly.MaxValue)
            return slice.Window.ResetsAt is { } lastReset && now >= lastReset && slice.ObservedThrough < lastReset;
        var dayEnd = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), JapaneseCalendar.Offset);
        var end = slice.Window.ResetsAt is { } reset && reset < dayEnd ? reset : dayEnd;
        return now >= end && slice.ObservedThrough < end;
    }

    public static IReadOnlyList<HistoryRecord> Import(UsageState state)
    {
        state.Validate();
        return state.Deltas.Select(delta =>
        {
            var snapshot = state.Baselines.Concat(state.Latest).FirstOrDefault(s =>
                s.Key == delta.Key && s.PeriodId == delta.PeriodId)
                ?? throw new UsageException(FailureKind.Storage, "history_import_definition_missing");
            // Old state kept deltas, not every cumulative snapshot. Never invent a historical used value.
            return HistoryRecord.Create(snapshot with
            {
                ObservedAt = delta.To, Used = null, UsedPercent = null, CreditsUsed = null
            }, delta);
        }).DistinctBy(r => r.Id).OrderBy(r => r.Delta.To).ToArray();
    }

    public static IReadOnlyList<DailyHistorySlice> Project(
        DateOnly date, IEnumerable<HistoryRecord> source, TimeSpan maximumGap)
    {
        if (maximumGap <= TimeSpan.Zero)
            throw new UsageException(FailureKind.Schema, "history_maximum_gap");
        var records = source.DistinctBy(r => r.Id).ToArray();
        foreach (var record in records) record.Validate();
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), JapaneseCalendar.Offset);
        var dayEnd = dayStart.AddDays(1);
        var output = new List<DailyHistorySlice>();
        foreach (var group in records.Where(r => r.AffectedDays().Contains(date))
            .GroupBy(r => (r.Snapshot.Key, r.Snapshot.PeriodId)))
        {
            var snapshot = group.First().Snapshot;
            var start = snapshot.Window.StartsAt is { } a && a > dayStart ? a : dayStart;
            var end = snapshot.Window.ResetsAt is { } b && b < dayEnd ? b : dayEnd;
            if (end <= start) continue;
            var deltas = group.Select(r => r.Delta).OrderBy(d => d.From).ThenBy(d => d.To).ToArray();
            var measurable = deltas.Where(d => d.Kind == DeltaKind.Consumption &&
                d.From >= start && d.To <= end && d.To > d.From).ToArray();
            var observations = group.Select(r => r.Snapshot.ObservedAt)
                .Where(t => t >= start && t <= end).Order().ToArray();
            DateTimeOffset? through = observations.Length == 0 ? null : observations[^1];
            var cursor = start;
            bool contiguous = true;
            foreach (var delta in measurable)
            {
                if (delta.From < cursor)
                    throw new UsageException(FailureKind.Storage, "history_consumption_intervals_overlap");
                if (delta.From != cursor || delta.To - delta.From > maximumGap) contiguous = false;
                cursor = delta.To;
            }
            bool uncertain = deltas.Any(d => d.Kind == DeltaKind.Consumption
                ? d.From < start || d.To > end
                : d.Kind != DeltaKind.Baseline || d.To > start);
            var coverage = measurable.Length == 0 ? Coverage.Unmeasured
                : contiguous && !uncertain && through == cursor
                    ? Coverage.CompleteToObservation : Coverage.Partial;
            var daily = Budgets.Calculate(snapshot)?.Daily;
            output.Add(new DailyHistorySlice(date, snapshot.Key, snapshot.PeriodId, snapshot.Plan, snapshot.Unit, snapshot.Window,
                measurable.Length == 0 ? null : measurable.Sum(d => d.Amount!.Value),
                daily * (decimal)(end - start).TotalDays, coverage,
                observations.Length == 0 ? null : observations[0], through,
                deltas.Where(d => d.Kind != DeltaKind.Consumption).Select(d => d.Kind).Distinct().ToArray(),
                deltas.Any(d => d.Kind == DeltaKind.Consumption && (d.From < start || d.To > end)),
                !contiguous)
            {
                Cumulative = CumulativeAtEndOfDay(date, snapshot.Key, snapshot.PeriodId, through, group),
                CumulativeCaptured = true
            });
        }
        return output.OrderBy(s => s.Key.Account.Provider).ThenBy(s => s.Key.Account.Id)
            .ThenBy(s => s.Key.Bucket).ThenBy(s => s.PeriodId).ToArray();
    }
}
