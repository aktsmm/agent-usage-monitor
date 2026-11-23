using System.Text.Json;
using System.Text.Json.Nodes;
using AiUsageTray.Core;
using AiUsageTray.Desktop;

static class HistoryTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-12T00:00:00+09:00");
    private static readonly QuotaKey Key = new(AccountKey.FromIdentity(Provider.Copilot, "synthetic-history"), "monthly");
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(10);
    private static readonly DateOnly Day = JapaneseCalendar.Date(At);

    private static QuotaSnapshot Snapshot(DateTimeOffset at) => new(Key, "synthetic", UsageSource.CopilotHttp,
        at, UsageUnit.PercentagePoints, 20, 100, 20, false,
        new(WindowKind.Monthly, At.AddDays(-11), At.AddDays(20)));

    private static HistoryRecord Record(DateTimeOffset from, DateTimeOffset to, decimal? amount = 1,
        DeltaKind kind = DeltaKind.Consumption, QuotaSnapshot? snapshot = null)
    {
        var s = snapshot ?? Snapshot(to);
        return HistoryRecord.Create(s, new(s.Key, s.PeriodId, from, to, s.Unit, amount, kind));
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("History assertion failed");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"History expected {expected}; actual {actual}");
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (UsageException) { return; }
        throw new InvalidOperationException("Expected explicit history failure");
    }

    public static void Add(List<(string Name, Func<Task> Run)> tests)
    {
        void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
        void TestFiles(string name, Func<Fixture, Task> run) => tests.Add((name, async () =>
        {
            using var fixture = new Fixture();
            await run(fixture);
        }));

        Test("history cumulative snapshots are not summed", () =>
        {
            var records = new[] { Record(At, At.AddMinutes(5), 2), Record(At.AddMinutes(5), At.AddMinutes(10), 3) };
            var result = DailyHistory.Project(Day, records, Gap).Single();
            Equal<decimal?>(5, result.Observed);
            Equal<decimal?>(20, result.Cumulative);
            Equal(Coverage.CompleteToObservation, result.Coverage);
            Equal<decimal?>(100m / 31, result.Guidance);
        });
        TestFiles("history writer retries concurrent readers without changing their snapshots", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var rawPath = Path.Combine(f.Store.Root, "months", "2026-10", "2026-10-12.json");
            var summaryPath = f.Store.SummaryPath(Day);
            var catalogPath = Path.Combine(f.Store.Root, "catalog.json");
            var historyFiles = typeof(HistoryStore).Assembly.GetType("AiUsageTray.Core.HistoryFiles")!;
            var openRead = historyFiles.GetMethod("OpenRead",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            using var raw = (FileStream)openRead.Invoke(null, [rawPath])!;
            using var summary = (FileStream)openRead.Invoke(null, [summaryPath])!;
            using var catalog = (FileStream)openRead.Invoke(null, [catalogPath])!;
            try
            {
                using var inPlace = new FileStream(rawPath, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                throw new InvalidOperationException("Snapshot reader allowed in-place modification");
            }
            catch (IOException) { }
            var oldRaw = await JsonSerializer.DeserializeAsync<HistoryRawDay>(raw, StateStore.JsonOptions);
            var oldSummary = await JsonSerializer.DeserializeAsync<HistoryMonth>(summary, StateStore.JsonOptions);
            var oldCatalog = await JsonSerializer.DeserializeAsync<HistoryCatalog>(catalog, StateStore.JsonOptions);
            Equal(1, oldRaw!.Records.Count);
            Equal<decimal?>(1, oldSummary!.Slices.Single().Observed);
            Equal(At.AddMinutes(5), oldCatalog!.Items.Single().LastObservation);
            var pending = f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10)), Gap, default);
            await Task.Delay(150);
            if (pending.IsCompleted) await pending;
            True(!pending.IsCompleted);
            raw.Dispose();
            summary.Dispose();
            catalog.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var current = await f.Store.QueryAsync(new(Day, Day, Key), default);
            Equal<decimal?>(2, current.Single().Slices.Single().Observed);
        });
        TestFiles("history journal preserves pending data after persistent replacement conflict", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var journal = new HistoryJournal(f.Profile, f.Writer);
            await journal.EnqueueAsync([Record(At.AddMinutes(5), At.AddMinutes(10))], default);
            var catalogPath = Path.Combine(f.Store.Root, "catalog.json");
            var historyFiles = typeof(HistoryStore).Assembly.GetType("AiUsageTray.Core.HistoryFiles")!;
            var openRead = historyFiles.GetMethod("OpenRead",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            using var catalog = (FileStream)openRead.Invoke(null, [catalogPath])!;
            var blocked = await journal.FlushAsync(f.Store, Gap, default);
            True(!blocked.Saved && blocked.PendingRecords == 1 &&
                blocked.Error!.StartsWith("history_storage_unavailable:", StringComparison.Ordinal));
            var current = await f.Store.QueryAsync(new(Day, Day, Key), default);
            Equal<decimal?>(1, current.Single().Slices.Single().Observed);
            True(!Directory.EnumerateFiles(f.Store.Root, "*.tmp", SearchOption.AllDirectories).Any());
            catalog.Dispose();
            True((await journal.FlushAsync(f.Store, Gap, default)).Saved);
            Equal<decimal?>(2, (await f.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
        });
        TestFiles("history replacement retry honours cancellation and cleans temporary files", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var catalogPath = Path.Combine(f.Store.Root, "catalog.json");
            var historyFiles = typeof(HistoryStore).Assembly.GetType("AiUsageTray.Core.HistoryFiles")!;
            var openRead = historyFiles.GetMethod("OpenRead",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            using var catalog = (FileStream)openRead.Invoke(null, [catalogPath])!;
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                await f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10)), Gap, cancelled.Token);
                throw new InvalidOperationException("Expected cancelled replacement");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            var current = await f.Store.QueryAsync(new(Day, Day, Key), default);
            Equal<decimal?>(1, current.Single().Slices.Single().Observed);
            True(!Directory.EnumerateFiles(f.Store.Root, "*.tmp", SearchOption.AllDirectories).Any());
        });
        Test("history cumulative uses the latest actual observation, including zero and unknown", () =>
        {
            var first = Snapshot(At.AddMinutes(5)) with { Used = 20 };
            var adjusted = Snapshot(At.AddMinutes(10)) with { Used = 0 };
            var records = new[]
            {
                Record(At, first.ObservedAt, 2, snapshot: first),
                Record(first.ObservedAt, adjusted.ObservedAt, null, DeltaKind.Adjustment, adjusted)
            };
            Equal<decimal?>(0, DailyHistory.Project(Day, records, Gap).Single().Cumulative);
            var unknown = Snapshot(At.AddMinutes(15)) with { Used = null, UsedPercent = null };
            var later = records.Append(Record(adjusted.ObservedAt, unknown.ObservedAt, null, DeltaKind.Adjustment, unknown));
            Equal<decimal?>(null, DailyHistory.Project(Day, later, Gap).Single().Cumulative);
        });
        Test("history cumulative keeps accounts and period definitions separate", () =>
        {
            var one = Snapshot(At.AddMinutes(5)) with { Used = 12 };
            var other = one with { Key = new(AccountKey.FromIdentity(Provider.Copilot, "other-cumulative"), "monthly"), Used = 30 };
            var changed = one with { Plan = "different", Used = 40 };
            var slices = DailyHistory.Project(Day, new[]
            {
                Record(At, one.ObservedAt, 1, snapshot: one),
                Record(At, other.ObservedAt, 1, snapshot: other),
                Record(At, changed.ObservedAt, 1, snapshot: changed)
            }, Gap);
            Equal<decimal?>(12, slices.Single(s => s.Key == Key && s.Plan == "synthetic").Cumulative);
            Equal<decimal?>(30, slices.Single(s => s.Key == other.Key).Cumulative);
            Equal<decimal?>(40, slices.Single(s => s.Plan == "different").Cumulative);
        });
        Test("history conflicting snapshots at the same instant are not invented", () =>
        {
            var one = Snapshot(At.AddMinutes(5)) with { Used = 12 };
            var other = one with { Used = 30 };
            var result = DailyHistory.Project(Day, new[]
            {
                Record(one.ObservedAt, one.ObservedAt, null, DeltaKind.Baseline, one),
                Record(other.ObservedAt, other.ObservedAt, null, DeltaKind.Adjustment, other)
            }, Gap).Single();
            Equal<decimal?>(null, result.Cumulative);
        });
        TestFiles("history query restores actual cumulative values from legacy raw without changing summaries", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var path = f.Store.SummaryPath(Day);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var slices = json["Slices"]!.AsArray();
            slices[0]!.AsObject().Remove("Cumulative");
            slices[0]!.AsObject().Remove("CumulativeCaptured");
            var legacySummary = json.ToJsonString();
            var later = Snapshot(At.AddMinutes(10)) with { Used = 30 };
            await f.Store.AppendAsync(Record(At.AddMinutes(5), later.ObservedAt, 10, snapshot: later), Gap, default);
            await File.WriteAllTextAsync(path, legacySummary);
            var before = await File.ReadAllBytesAsync(path);
            var page = await f.Store.QueryPageAsync(new(Day, Day, Key), 0, null, default);
            Equal<decimal?>(20, page.Days.Single().Slices.Single().Cumulative);
            using (var frame = new MemoryStream())
            {
                await PipeProtocol.WriteAsync(frame, new DesktopResponse(true,
                    History: new HistoryResponse(true, DaysPage: page)), default);
                frame.Position = 0;
                var response = await PipeProtocol.ReadAsync<DesktopResponse>(frame, default);
                Equal<decimal?>(20, response.History!.DaysPage!.Days.Single().Slices.Single().Cumulative);
            }
            var after = await File.ReadAllBytesAsync(path);
            True(before.SequenceEqual(after));
            var missingPath = Path.Combine(f.Store.Root, "months", "2026-10", "2026-10-12.json");
            File.Delete(missingPath);
            var missing = await f.Store.QueryAsync(new(Day, Day, Key), default);
            Equal<decimal?>(null, missing.Single().Slices.Single().Cumulative);
        });
        TestFiles("legacy history query reads verified archive when raw is unavailable", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            await f.Store.ArchiveAsync(Day, At.AddMonths(2), default);
            var path = f.Store.SummaryPath(Day);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var slice = json["Slices"]![0]!.AsObject();
            slice.Remove("Cumulative");
            slice.Remove("CumulativeCaptured");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            File.Delete(Path.Combine(f.Store.Root, "months", "2026-10", "2026-10-12.json"));
            var recovered = await f.Store.QueryAsync(new(Day, Day, Key), default);
            Equal<decimal?>(20, recovered.Single().Slices.Single().Cumulative);
            await File.WriteAllTextAsync(f.Store.ArchivePath(Day), "invalid");
            await Reject(() => f.Store.QueryAsync(new(Day, Day, Key), default));
        });
        TestFiles("legacy history uses each day's raw when another day requires an archive", async f =>
        {
            var nextAt = At.AddDays(1);
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            await f.Store.AppendAsync(Record(nextAt, nextAt.AddMinutes(5)), Gap, default);
            await f.Store.ArchiveAsync(Day, At.AddMonths(2), default);

            var summaryPath = f.Store.SummaryPath(Day);
            var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
            foreach (var slice in summary["Slices"]!.AsArray())
            {
                slice!.AsObject().Remove("Cumulative");
                slice.AsObject().Remove("CumulativeCaptured");
            }
            await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
            var originalSummary = await File.ReadAllBytesAsync(summaryPath);
            var archivePath = f.Store.ArchivePath(Day);
            var originalArchive = await File.ReadAllBytesAsync(archivePath);

            var monthFolder = Path.GetDirectoryName(summaryPath)!;
            var existingPath = Path.Combine(monthFolder, "2026-10-12.json");
            var missingPath = Path.Combine(monthFolder, "2026-10-13.json");
            var existing = JsonNode.Parse(await File.ReadAllTextAsync(existingPath))!;
            existing["Records"]![0]!["Snapshot"]!["Used"] = 77;
            await File.WriteAllTextAsync(existingPath, existing.ToJsonString());
            File.Delete(missingPath);

            var days = await f.Store.QueryAsync(new(Day, Day.AddDays(1), Key), default);
            Equal<decimal?>(77, days[0].Slices.Single().Cumulative);
            Equal<decimal?>(20, days[1].Slices.Single().Cumulative);

            await File.WriteAllTextAsync(existingPath, "{invalid");
            try
            {
                await f.Store.QueryAsync(new(Day, Day.AddDays(1), Key), default);
                throw new InvalidOperationException("Expected invalid raw history error");
            }
            catch (UsageException ex) when (ex.SafeCode == "history_json_invalid") { }
            Equal("{invalid", await File.ReadAllTextAsync(existingPath));
            var finalSummary = await File.ReadAllBytesAsync(summaryPath);
            var finalArchive = await File.ReadAllBytesAsync(archivePath);
            True(originalSummary.SequenceEqual(finalSummary));
            True(originalArchive.SequenceEqual(finalArchive));
        });
        TestFiles("history overview keeps accounts, units, periods, zero and missing days separate", async f =>
        {
            var window = new QuotaWindow(WindowKind.Monthly, At.AddDays(-20), At.AddDays(40));
            var first = Snapshot(At.AddMinutes(5)) with { Window = window };
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5), 2, snapshot: first), Gap, default);
            var zero = first with { Key = new(AccountKey.FromIdentity(Provider.Copilot, "other-account"), "other") };
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5), 0, snapshot: zero), Gap, default);
            var changed = first with { Unit = UsageUnit.Credits, Total = 200, ObservedAt = At.AddMinutes(10) };
            await f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10), 3, snapshot: changed), Gap, default);
            var page = await f.Store.OverviewAsync(Day, Day.AddDays(1), 0, null, default);
            Equal(3, page.Items.Count);
            Equal<decimal?>(2, page.Items.Single(i => i.Key == Key && i.Unit == UsageUnit.PercentagePoints).Observed);
            Equal<decimal?>(3, page.Items.Single(i => i.Unit == UsageUnit.Credits).Observed);
            var zeroItem = page.Items.Single(i => i.Key == zero.Key);
            Equal<decimal?>(0, zeroItem.Observed);
            Equal(1, zeroItem.UnmeasuredDays);
            var empty = await f.Store.OverviewAsync(Day.AddDays(3), Day.AddDays(3), 0, null, default);
            True(empty.Items.All(i => i.Observed is null));
        });
        TestFiles("history overview paging stays consistent and rejects a replaced snapshot", async f =>
        {
            for (int i = 0; i < 41; i++)
            {
                var s = Snapshot(At.AddMinutes(5)) with
                {
                    Key = new(AccountKey.FromIdentity(Provider.Copilot, "page-" + i), "monthly")
                };
                await f.Store.AppendAsync(Record(At, At.AddMinutes(5), 1, snapshot: s), Gap, default);
            }
            var first = await f.Store.OverviewAsync(Day, Day, 0, null, default);
            Equal(40, first.Items.Count);
            Equal<int?>(40, first.NextOffset);
            using (var frame = new MemoryStream())
            {
                await PipeProtocol.WriteAsync(frame, new DesktopResponse(true,
                    History: new HistoryResponse(true, Overview: first)), default);
                True(frame.Length < PipeProtocol.MaximumMessageBytes);
                frame.Position = 0;
                var decoded = await PipeProtocol.ReadAsync<DesktopResponse>(frame, default);
                Equal(40, decoded.History!.Overview!.Items.Count);
                Equal(first.Snapshot, decoded.History.Overview.Snapshot);
            }
            var last = await f.Store.OverviewAsync(Day, Day, 40, first.Snapshot, default);
            Equal(1, last.Items.Count);
            Equal<int?>(null, last.NextOffset);
            var extra = Snapshot(At.AddMinutes(5)) with
            {
                Key = new(AccountKey.FromIdentity(Provider.Codex, "page-extra"), "monthly")
            };
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5), 1, snapshot: extra), Gap, default);
            var preserved = await f.Store.OverviewAsync(Day, Day, 40, first.Snapshot, default);
            Equal(1, preserved.Items.Count);
            await f.Store.OverviewAsync(Day, Day, 0, null, default);
            await Reject(() => f.Store.OverviewAsync(Day, Day, 40, first.Snapshot, default));
        });
        TestFiles("history overview does not invent day boundary usage across months", async f =>
        {
            var midnight = new DateTimeOffset(2026, 11, 1, 0, 0, 0, JapaneseCalendar.Offset);
            var s = Snapshot(midnight.AddMinutes(5)) with
            {
                Window = new(WindowKind.Monthly, midnight.AddDays(-20), midnight.AddDays(20))
            };
            await f.Store.AppendAsync(Record(midnight.AddMinutes(-5), midnight.AddMinutes(5), 12, snapshot: s),
                Gap, default);
            var page = await f.Store.OverviewAsync(new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 1), 0, null, default);
            var item = page.Items.Single();
            Equal<decimal?>(null, item.Observed);
            Equal(2, item.UnmeasuredDays);
        });
        TestFiles("history overview does not mark future days as missing", async f =>
        {
            var today = JapaneseCalendar.Date(DateTimeOffset.UtcNow);
            var midnight = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), JapaneseCalendar.Offset);
            var snapshot = Snapshot(midnight.AddMinutes(5)) with
            {
                Window = new(WindowKind.Monthly, midnight.AddDays(-1), midnight.AddDays(20))
            };
            await f.Store.AppendAsync(Record(midnight, midnight.AddMinutes(5), 1, snapshot: snapshot), Gap, default);
            var page = await f.Store.OverviewAsync(today.AddDays(-1), today.AddDays(10), 0, null, default);
            Equal(1, page.Items.Single().UnmeasuredDays);
        });
        TestFiles("history definition changes keep amounts separate and coverage conservative", async f =>
        {
            var window = new QuotaWindow(WindowKind.Monthly, At.AddDays(-1), At.AddDays(4));
            var first = Snapshot(At.AddMinutes(5)) with { Window = window, Plan = "first" };
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5), 2, snapshot: first), Gap, default);
            var changedAt = At.AddDays(2);
            var next = first with { Plan = "second", ObservedAt = changedAt };
            await f.Store.AppendAsync(Record(changedAt, changedAt, null, DeltaKind.DefinitionChanged, next), Gap, default);
            await f.Store.AppendAsync(Record(changedAt, changedAt.AddMinutes(5), 3,
                snapshot: next with { ObservedAt = changedAt.AddMinutes(5) }), Gap, default);
            var page = await f.Store.OverviewAsync(Day, Day.AddDays(2), 0, null, default);
            var earlier = page.Items.Single(item => item.Plan == "first");
            var later = page.Items.Single(item => item.Plan == "second");
            Equal<decimal?>(2, earlier.Observed);
            Equal<decimal?>(3, later.Observed);
            True(earlier.UnmeasuredDays > 0 && later.UnmeasuredDays > 0);
            True(later.PartialDays > 0);
        });
        TestFiles("history query handles the last representable month", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var first = new DateOnly(9999, 12, 1);
            var last = DateOnly.MaxValue;
            Equal(31, (await f.Store.QueryAsync(new(first, last, Key), default)).Count);
            var overview = await f.Store.OverviewAsync(first, last, 0, null, default);
            Equal<decimal?>(null, overview.Items.Single().Observed);
        });
        TestFiles("history detail handles many period definitions within IPC limits", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var source = Enumerable.Range(0, 1500).Select(i => new DailyHistorySlice(
                Day, Key, $"period-{i:D4}", new string('p', 700), UsageUnit.PercentagePoints,
                Snapshot(At).Window, i, null, Coverage.Partial, At, At.AddMinutes(5), [])).ToList();
            await File.WriteAllTextAsync(f.Store.SummaryPath(Day),
                JsonSerializer.Serialize(new HistoryMonth(1, f.Writer, DailyHistory.Month(Day), source),
                    StateStore.JsonOptions));
            var query = new HistoryQuery(Day, Day.AddDays(1), Key);
            var result = await f.Store.QueryAsync(query, default);
            using (var oversized = new MemoryStream())
            {
                var rejected = false;
                try
                {
                    await PipeProtocol.WriteAsync(oversized, new DesktopResponse(true,
                        History: new HistoryResponse(true, Days: result.ToList())), default);
                }
                catch (UsageException ex) when (ex.SafeCode == "ipc_message_too_large") { rejected = true; }
                True(rejected);
            }
            var received = new List<HistoryDay>();
            string? snapshot = null;
            int offset = 0, pageCount = 0;
            do
            {
                var page = await f.Store.QueryPageAsync(query, offset, snapshot, default);
                using var frame = new MemoryStream();
                await PipeProtocol.WriteAsync(frame, new DesktopResponse(true,
                    History: new HistoryResponse(true, DaysPage: page)), default);
                True(frame.Length < PipeProtocol.MaximumMessageBytes);
                frame.Position = 0;
                var decoded = (await PipeProtocol.ReadAsync<DesktopResponse>(frame, default)).History!.DaysPage!;
                if (snapshot is not null) Equal(snapshot, decoded.Snapshot);
                snapshot = decoded.Snapshot;
                received.AddRange(decoded.Days);
                pageCount++;
                if (decoded.NextOffset is not { } next) break;
                True(next > offset);
                offset = next;
            } while (true);
            True(pageCount > 1);
            var merged = received.GroupBy(day => day.Date).OrderBy(g => g.Key)
                .Select(g => new HistoryDay(g.Key, g.SelectMany(day => day.Slices).ToArray())).ToArray();
            Equal(2, merged.Length);
            Equal(0, merged[1].Slices.Count);
            Equal(source.Count, merged[0].Slices.Count);
            True(source.Select(s => (s.PeriodId, s.Observed))
                .SequenceEqual(merged[0].Slices.Select(s => (s.PeriodId, s.Observed))));
            var before = await f.Store.QueryPageAsync(query, 40, snapshot, default);
            await f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10)), Gap, default);
            var preserved = await f.Store.QueryPageAsync(query, 40, snapshot, default);
            True(before.Days.SelectMany(d => d.Slices).Select(s => s.PeriodId)
                .SequenceEqual(preserved.Days.SelectMany(d => d.Slices).Select(s => s.PeriodId)));
            await f.Store.QueryPageAsync(query, 0, null, default);
            await Reject(() => f.Store.QueryPageAsync(query, 40, snapshot, default));
            var refreshed = await f.Store.QueryPageAsync(query, 0, null, default);
            await f.Store.QueryPageAsync(new(Day.AddDays(1), Day.AddDays(1), Key), 0, null, default);
            await Reject(() => f.Store.QueryPageAsync(query, 1, refreshed.Snapshot, default));
        });
        TestFiles("history pages accept large individual items that fit the IPC frame", async f =>
        {
            var service = await HistoryService.LoadAsync(f.Profile, default);
            var snapshot = Snapshot(At.AddMinutes(5)) with { Plan = new string('p', 350_000) };
            await service.RecordAsync([Record(At, At.AddMinutes(5), snapshot: snapshot)], Gap, default);
            var commands = new HistoryCommands(service);
            var overview = await commands.ExecuteAsync(new(HistoryAction.Overview, From: Day, Through: Day), default);
            True(overview.Success);
            Equal(snapshot.Plan, overview.Overview!.Items.Single().Plan);
            var detail = await commands.ExecuteAsync(new(HistoryAction.QueryPage,
                Query: new(Day, Day, Key)), default);
            True(detail.Success);
            Equal(snapshot.Plan, detail.DaysPage!.Days.Single().Slices.Single().Plan);
            foreach (var response in new[] { overview, detail })
            {
                using var frame = new MemoryStream();
                await PipeProtocol.WriteAsync(frame, new DesktopResponse(true, History: response), default);
                True(frame.Length - 4 <= PipeProtocol.MaximumMessageBytes);
            }
            var enormous = snapshot with { Plan = new string('q', 550_000), ObservedAt = At.AddMinutes(10) };
            await service.RecordAsync([Record(At.AddMinutes(5), At.AddMinutes(10), snapshot: enormous)], Gap, default);
            var first = await commands.ExecuteAsync(new(HistoryAction.Overview, From: Day, Through: Day), default);
            True(first.Success);
            var firstOverview = first.Overview ?? throw new InvalidOperationException("Overview page missing");
            var nextOverview = firstOverview.NextOffset ?? throw new InvalidOperationException("Overview continuation missing");
            var tooLarge = await commands.ExecuteAsync(new(HistoryAction.Overview, From: Day, Through: Day,
                Offset: nextOverview, Snapshot: firstOverview.Snapshot), default);
            True(!tooLarge.Success && tooLarge.Error == "ipc_message_too_large");
            var other = Snapshot(At.AddMinutes(5)) with
            {
                Key = new(AccountKey.FromIdentity(Provider.Codex, "other-account"), "other")
            };
            await service.RecordAsync([Record(At, At.AddMinutes(5), 2, snapshot: other)], Gap, default);
            var catalog = await commands.ExecuteAsync(new(HistoryAction.Catalog), default);
            True(catalog.Success && catalog.Catalog!.Any(item => item.Key == other.Key));
            var unrelated = await commands.ExecuteAsync(new(HistoryAction.QueryPage,
                Query: new(Day, Day, other.Key)), default);
            True(unrelated.Success && unrelated.DaysPage!.Days.Single().Slices.Single().Observed == 2);
            var query = new HistoryQuery(Day, Day, Key);
            var firstDay = await commands.ExecuteAsync(new(HistoryAction.QueryPage, Query: query), default);
            True(firstDay.Success);
            var firstDayPage = firstDay.DaysPage ?? throw new InvalidOperationException("Detail page missing");
            var nextDay = firstDayPage.NextOffset ?? throw new InvalidOperationException("Detail continuation missing");
            var tooLargeDay = await commands.ExecuteAsync(new(HistoryAction.QueryPage, Query: query,
                Offset: nextDay, Snapshot: firstDayPage.Snapshot), default);
            True(!tooLargeDay.Success && tooLargeDay.Error == "ipc_message_too_large");
            foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
            {
                var localizer = new UiLocalizer(language);
                Equal(localizer.Text(UiText.HistoryResponseTooLarge) + " (ipc_message_too_large)",
                    localizer.Error(tooLarge.Error!));
            }
        });
        TestFiles("history pages shrink when the full response cannot fit", async f =>
        {
            var first = Snapshot(At.AddMinutes(5));
            var second = first with { Plan = "another plan", ObservedAt = At.AddMinutes(10) };
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5), snapshot: first), Gap, default);
            await f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10), snapshot: second), Gap, default);

            var overview = await f.Store.OverviewAsync(Day, Day, 0, null, default, page => page.Items.Count <= 1);
            Equal(1, overview.Items.Count);
            var nextOverview = overview.NextOffset ?? throw new InvalidOperationException("Overview continuation missing");
            var remaining = await f.Store.OverviewAsync(Day, Day, nextOverview, overview.Snapshot, default,
                page => page.Items.Count <= 1);
            Equal(1, remaining.Items.Count);
            True(overview.Items[0].PeriodId != remaining.Items[0].PeriodId);

            var query = new HistoryQuery(Day, Day, Key);
            var detail = await f.Store.QueryPageAsync(query, 0, null, default, page => page.Days.Count <= 1);
            Equal(1, detail.Days.Count);
            var nextDetail = detail.NextOffset ?? throw new InvalidOperationException("Detail continuation missing");
            var rest = await f.Store.QueryPageAsync(query, nextDetail, detail.Snapshot, default,
                page => page.Days.Count <= 1);
            Equal(1, rest.Days.Count);
            True(detail.Days[0].Slices[0].PeriodId != rest.Days[0].Slices[0].PeriodId);
        });
        Test("history marks a short window partial when it ended today before observation", () =>
        {
            var slice = new DailyHistorySlice(Day, Key, "short", null, UsageUnit.PercentagePoints,
                new(WindowKind.Short, At, At.AddHours(9)), 1, null, Coverage.CompleteToObservation,
                At, At.AddHours(8), []);
            True(!DailyHistory.IsPartial(Day, slice, At.AddHours(8)));
            True(DailyHistory.IsPartial(Day, slice, At.AddHours(12)));
        });
        Test("history partial coverage handles the final representable day", () =>
        {
            var start = new DateTimeOffset(9999, 12, 31, 0, 0, 0, JapaneseCalendar.Offset);
            var slice = new DailyHistorySlice(DateOnly.MaxValue, Key, "short", null, UsageUnit.PercentagePoints,
                new(WindowKind.Short, start, start.AddHours(11)), 1, null, Coverage.CompleteToObservation,
                start, start.AddHours(10), []);
            True(!DailyHistory.IsPartial(DateOnly.MaxValue, slice, start.AddHours(10)));
            True(DailyHistory.IsPartial(DateOnly.MaxValue, slice, start.AddHours(12)));
        });
        tests.Add(("history overlapping consumption intervals fail instead of double counting", () =>
            Reject(() => Task.FromResult(DailyHistory.Project(Day,
                [Record(At, At.AddMinutes(5)), Record(At.AddMinutes(1), At.AddMinutes(6))], Gap)))));
        Test("history missing day and observed rounded zero differ", () =>
        {
            Equal(0, DailyHistory.Project(Day, [], Gap).Count);
            var result = DailyHistory.Project(Day, [Record(At, At.AddMinutes(5), 0)], Gap).Single();
            Equal<decimal?>(0, result.Observed);
            Equal(Coverage.CompleteToObservation, result.Coverage);
        });
        Test("history day crossing delta is not guessed or split", () =>
        {
            var crossing = Record(At.AddMinutes(-5), At.AddMinutes(5), 20);
            foreach (var day in new[] { Day.AddDays(-1), Day })
            {
                var slice = DailyHistory.Project(day, [crossing], Gap).Single();
                Equal<decimal?>(null, slice.Observed);
                True(slice.UnallocatedBoundary);
            }
            var partial = DailyHistory.Project(Day, [crossing, Record(At.AddMinutes(5), At.AddMinutes(10), 1)], Gap).Single();
            Equal<decimal?>(1, partial.Observed);
            Equal(Coverage.Partial, partial.Coverage);
        });
        Test("history exact midnight endpoint belongs to preceding day", () =>
        {
            var record = Record(At.AddMinutes(-5), At, 2);
            Equal<decimal?>(2, DailyHistory.Project(Day.AddDays(-1), [record], Gap).Single().Observed);
            Equal<decimal?>(null, DailyHistory.Project(Day, [record], Gap).Single().Observed);
        });
        Test("history startup and sleep leave partial coverage", () =>
        {
            var result = DailyHistory.Project(Day, [Record(At.AddHours(1), At.AddHours(2), 2)], Gap).Single();
            Equal(Coverage.Partial, result.Coverage);
            True(result.HasGaps);
        });
        Test("history plan denominator unit and provider slices stay isolated", () =>
        {
            var a = Record(At, At.AddMinutes(5));
            var s = Snapshot(At.AddMinutes(10)) with { Plan = "changed", Total = 200, Unit = UsageUnit.Credits };
            var b = Record(At.AddMinutes(5), s.ObservedAt, 2, snapshot: s);
            var other = s with { Key = new(AccountKey.FromIdentity(Provider.Codex, "synthetic-history"), "weekly") };
            var c = Record(At.AddMinutes(5), s.ObservedAt, 3, snapshot: other);
            var slices = DailyHistory.Project(Day, [a, b, c], Gap);
            Equal(3, slices.Count);
            True(slices.Any(x => x.Unit == UsageUnit.Credits && x.Observed == 2));
            True(slices.Any(x => x.Unit == UsageUnit.PercentagePoints && x.Observed == 1));
        });
        Test("history reset and definition change are not consumption", () =>
        {
            var reset = Record(At, At.AddMinutes(5), null, DeltaKind.Reset);
            var changed = Record(At.AddMinutes(5), At.AddMinutes(10), null, DeltaKind.DefinitionChanged);
            var slice = DailyHistory.Project(Day, [reset, changed], Gap).Single();
            Equal<decimal?>(null, slice.Observed);
            True(slice.Events.Contains(DeltaKind.Reset) && slice.Events.Contains(DeltaKind.DefinitionChanged));
        });
        Test("history week and provider reset boundaries keep fixed guidance", () =>
        {
            var weekly = Snapshot(At.AddMinutes(5)) with { Window = new(WindowKind.Weekly, At, At.AddDays(7)) };
            var slice = DailyHistory.Project(Day, [Record(At, weekly.ObservedAt, 1, snapshot: weekly)], Gap).Single();
            Equal<decimal?>(100m / 7, slice.Guidance);
            Equal<decimal?>(7, slice.GuidancePercent);
            var next = weekly with { ObservedAt = At.AddDays(7).AddMinutes(5),
                Window = new(WindowKind.Weekly, At.AddDays(7), At.AddDays(14)) };
            True(weekly.PeriodId != next.PeriodId);
        });
        Test("history legacy import does not invent past cumulative values", () =>
        {
            var state = new UsageState();
            var initial = Snapshot(At);
            UsageEngine.Apply(state, new([initial], []), At);
            var next = initial with { ObservedAt = At.AddMinutes(5), Used = 22, UsedPercent = 22 };
            UsageEngine.Apply(state, new([next], []), next.ObservedAt);
            var imported = DailyHistory.Import(state);
            True(imported.All(r => r.Snapshot.Used is null && r.Snapshot.UsedPercent is null));
            Equal<decimal?>(2, imported.Last().Delta.Amount);
            Equal(imported.Last().Id, HistoryRecord.Create(next, state.Deltas.Last()).Id);
        });
        TestFiles("history duplicate replay and pending crash recovery are idempotent", async f =>
        {
            var journal = new HistoryJournal(f.Profile, f.Writer);
            var record = Record(At, At.AddMinutes(5), 2);
            await journal.EnqueueAsync([record, record], default);
            Equal(1, journal.PendingCount);
            await f.Store.AppendAsync(record, Gap, default);
            var restart = new HistoryJournal(f.Profile, f.Writer);
            True((await restart.FlushAsync(f.Store, Gap, default)).Saved);
            Equal<decimal?>(2, (await f.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
            await restart.EnqueueAsync([record], default);
            Equal(0, restart.PendingCount);
        });
        TestFiles("history raw saved before summary crash repairs on replay", async f =>
        {
            var record = Record(At, At.AddMinutes(5));
            await f.Store.AppendAsync(record, Gap, default);
            File.Delete(f.Store.SummaryPath(Day));
            await f.Store.AppendAsync(record, Gap, default);
            Equal<decimal?>(1, (await f.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
        });
        TestFiles("history summary repair preserves other days and missing catalog is explicit", async f =>
        {
            var first = Record(At, At.AddMinutes(5));
            var second = Record(At.AddDays(1), At.AddDays(1).AddMinutes(5), 2);
            await f.Store.AppendAsync(first, Gap, default);
            await f.Store.AppendAsync(second, Gap, default);
            File.Delete(f.Store.SummaryPath(Day));
            await Reject(() => f.Store.QueryAsync(new(Day, Day.AddDays(1), Key), default));
            await f.Store.AppendAsync(second, Gap, default);
            var days = await f.Store.QueryAsync(new(Day, Day.AddDays(1), Key), default);
            Equal<decimal?>(1, days[0].Slices.Single().Observed);
            Equal<decimal?>(2, days[1].Slices.Single().Observed);
            File.Delete(Path.Combine(f.Store.Root, "catalog.json"));
            await Reject(() => f.Store.CatalogAsync(default));
        });
        TestFiles("history destination offline retains queue and resumes without duplicates", async f =>
        {
            var journal = new HistoryJournal(f.Profile, f.Writer);
            await journal.EnqueueAsync([Record(At, At.AddMinutes(5), 3)], default);
            var blocked = Path.Combine(f.Folder, "blocked");
            await File.WriteAllTextAsync(blocked, "synthetic");
            var status = await journal.FlushAsync(new(Path.Combine(blocked, "history"), f.Writer), Gap, default);
            Equal(1, status.PendingRecords);
            True(!status.Saved && status.Error is not null);
            True((await journal.FlushAsync(f.Store, Gap, default)).Saved);
            True((await journal.FlushAsync(f.Store, Gap, default)).Saved);
            Equal<decimal?>(3, (await f.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
        });
        TestFiles("history month rotation and long queries read only requested summaries", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var unknown = Path.Combine(f.Store.Root, "months", "2010-01");
            Directory.CreateDirectory(unknown);
            await File.WriteAllTextAsync(Path.Combine(unknown, "summary.json"), "broken unrelated month");
            var days = await f.Store.QueryAsync(new(Day.AddDays(-89), Day, Key), default);
            Equal(90, days.Count);
            Equal(Coverage.Unmeasured, days[0].Coverage);
            await Reject(() => f.Store.QueryAsync(new(Day.AddDays(-93), Day, Key), default));
        });
        TestFiles("history cross-month raw shards preserve both boundaries", async f =>
        {
            var end = new DateTimeOffset(2026, 11, 1, 0, 0, 0, JapaneseCalendar.Offset);
            var s = Snapshot(end.AddMinutes(5)) with
            {
                Window = new(WindowKind.Monthly, At.AddDays(-11), end.AddDays(1))
            };
            var record = Record(end.AddMinutes(-5), end.AddMinutes(5), 10, snapshot: s);
            await f.Store.AppendAsync(record, Gap, default);
            var days = await f.Store.QueryAsync(new(new(2026, 10, 31), new(2026, 11, 1), Key), default);
            Equal(2, days.Count);
            True(days.All(d => d.Slices.Single().UnallocatedBoundary));
            Equal<decimal?>(null, days[0].Slices.Single().Cumulative);
            Equal<decimal?>(20, days[1].Slices.Single().Cumulative);
            True(File.Exists(f.Store.SummaryPath(new(2026, 10, 31))) && File.Exists(f.Store.SummaryPath(new(2026, 11, 1))));
            await f.Store.ArchiveAsync(new DateOnly(2026, 11, 1), end.AddMonths(1), default);
            await f.Store.AppendAsync(record, Gap, default);
            var replayed = await f.Store.QueryAsync(new(new(2026, 10, 31), new(2026, 11, 1), Key), default);
            True(replayed.All(d => d.Slices.Single().UnallocatedBoundary));
        });
        TestFiles("history cross-day shards in one month retain both summaries on replay", async f =>
        {
            var record = Record(At.AddMinutes(-5), At.AddMinutes(5));
            await f.Store.AppendAsync(record, Gap, default);
            await f.Store.AppendAsync(record, Gap, default);
            var days = await f.Store.QueryAsync(new(Day.AddDays(-1), Day, Key), default);
            Equal(2, days.Count);
            True(days.All(day => day.Slices.Count == 1 && day.Slices.Single().UnallocatedBoundary));
        });
        TestFiles("history foreign writer cannot overwrite a shared folder", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var other = new HistoryStore(f.Store.Root, Guid.NewGuid().ToString("N"));
            await Reject(() => other.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10)), Gap, default));
            Equal<decimal?>(1, (await f.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
        });
        TestFiles("history unknown schema and invalid JSON are explicit errors", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var path = f.Store.SummaryPath(Day);
            var text = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, text.Replace("\"Version\": 1", "\"Version\": 99"));
            await Reject(() => f.Store.QueryAsync(new(Day, Day, Key), default));
            await File.WriteAllTextAsync(path, "{");
            await Reject(() => f.Store.QueryAsync(new(Day, Day, Key), default));
        });
        TestFiles("history archive round-trip retains raw and summary with verified IDs", async f =>
        {
            var record = Record(At, At.AddMinutes(5), 4);
            await f.Store.AppendAsync(record, Gap, default);
            var path = await f.Store.ArchiveAsync(Day, At.AddMonths(1), default);
            True(File.Exists(path));
            True((await f.Store.VerifyArchiveAsync(Day, default)).Contains(record.Id));
            True(File.Exists(Path.Combine(f.Store.Root, "months", "2026-10", "2026-10-12.json")));
            Equal<decimal?>(4, (await f.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
            Equal(path, await f.Store.ArchiveAsync(Day, At.AddMonths(1), default));
            await f.Store.AppendAsync(record, Gap, default);
            Equal<decimal?>(4, (await f.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
            await Reject(() => f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10)), Gap, default));
        });
        TestFiles("history sealed month rejection does not change catalog", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            await f.Store.ArchiveAsync(Day, At.AddMonths(1), default);
            var catalogPath = Path.Combine(f.Store.Root, "catalog.json");
            var catalogBefore = await File.ReadAllBytesAsync(catalogPath);
            var other = Snapshot(At.AddMinutes(10)) with
            {
                Key = new(AccountKey.FromIdentity(Provider.Codex, "sealed-new-account"), "other")
            };
            try
            {
                await f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10), snapshot: other), Gap, default);
                throw new InvalidOperationException("Expected sealed-month failure");
            }
            catch (UsageException ex) { Equal("history_archived_month_is_sealed", ex.SafeCode); }
            var catalogAfter = await File.ReadAllBytesAsync(catalogPath);
            True(catalogBefore.SequenceEqual(catalogAfter));
            Equal(1, (await f.Store.CatalogAsync(default)).Items.Count);
        });
        TestFiles("history sealed month keeps pending journal without phantom catalog entry", async f =>
        {
            var service = await HistoryService.LoadAsync(f.Profile, default);
            await service.RecordAsync([Record(At, At.AddMinutes(5))], Gap, default);
            await service.ArchiveAsync(Day, At.AddMonths(1), default);
            var other = Snapshot(At.AddMinutes(10)) with
            {
                Key = new(AccountKey.FromIdentity(Provider.Codex, "pending-sealed-account"), "other")
            };
            await service.RecordAsync([Record(At.AddMinutes(5), At.AddMinutes(10), snapshot: other)], Gap, default);
            Equal(1, service.Status.PendingRecords);
            Equal("history_archived_month_is_sealed", service.Status.Error);
            True(!service.Status.Saved);
            Equal(1, (await service.Store.CatalogAsync(default)).Items.Count);
        });
        TestFiles("history cross-month rejection leaves earlier shard and catalog unchanged", async f =>
        {
            var boundary = new DateTimeOffset(2026, 11, 1, 0, 0, 0, JapaneseCalendar.Offset);
            var later = Snapshot(boundary.AddHours(1)) with
            {
                Window = new(WindowKind.Monthly, boundary.AddDays(-2), boundary.AddDays(40))
            };
            await f.Store.AppendAsync(Record(boundary.AddMinutes(55), boundary.AddHours(1), snapshot: later), Gap, default);
            await f.Store.ArchiveAsync(new DateOnly(2026, 11, 1), boundary.AddMonths(1), default);
            var catalogPath = Path.Combine(f.Store.Root, "catalog.json");
            var catalogBefore = await File.ReadAllBytesAsync(catalogPath);
            var previousDay = new DateOnly(2026, 10, 31);
            var crossing = later with { ObservedAt = boundary.AddMinutes(5) };
            try
            {
                await f.Store.AppendAsync(Record(boundary.AddMinutes(-5), boundary.AddMinutes(5),
                    snapshot: crossing), Gap, default);
                throw new InvalidOperationException("Expected sealed-month failure");
            }
            catch (UsageException ex) { Equal("history_archived_month_is_sealed", ex.SafeCode); }
            var catalogAfter = await File.ReadAllBytesAsync(catalogPath);
            True(catalogBefore.SequenceEqual(catalogAfter));
            True(!File.Exists(f.Store.SummaryPath(previousDay)));
            True(!Directory.Exists(Path.GetDirectoryName(f.Store.SummaryPath(previousDay))));
        });
        TestFiles("history rejected overlapping intervals do not poison raw records", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(10)), Gap, default);
            var rawPath = Path.Combine(f.Store.Root, "months", "2026-10", "2026-10-12.json");
            var catalogPath = Path.Combine(f.Store.Root, "catalog.json");
            var rawBefore = await File.ReadAllBytesAsync(rawPath);
            var catalogBefore = await File.ReadAllBytesAsync(catalogPath);
            try
            {
                await f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(15)), Gap, default);
                throw new InvalidOperationException("Expected overlap failure");
            }
            catch (UsageException ex) { Equal("history_consumption_intervals_overlap", ex.SafeCode); }
            var rawAfter = await File.ReadAllBytesAsync(rawPath);
            var catalogAfter = await File.ReadAllBytesAsync(catalogPath);
            True(rawBefore.SequenceEqual(rawAfter) && catalogBefore.SequenceEqual(catalogAfter));
            await f.Store.AppendAsync(Record(At.AddMinutes(10), At.AddMinutes(15)), Gap, default);
            Equal<decimal?>(2, (await f.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
        });
        TestFiles("overlapping history stays pending across retry", async f =>
        {
            var service = await HistoryService.LoadAsync(f.Profile, default);
            await service.RecordAsync([Record(At, At.AddMinutes(10))], Gap, default);
            await service.RecordAsync([Record(At.AddMinutes(5), At.AddMinutes(15))], Gap, default);
            Equal(1, service.Status.PendingRecords);
            Equal("history_consumption_intervals_overlap", service.Status.Error);
            True(!service.Status.Saved);
            await service.RetryAsync(Gap, default);
            Equal(1, service.Status.PendingRecords);
            Equal("history_consumption_intervals_overlap", service.Status.Error);
            Equal<decimal?>(1, (await service.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
        });
        TestFiles("history invalid gap does not write raw or catalog", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var rawPath = Path.Combine(f.Store.Root, "months", "2026-10", "2026-10-12.json");
            var catalogPath = Path.Combine(f.Store.Root, "catalog.json");
            var rawBefore = await File.ReadAllBytesAsync(rawPath);
            var catalogBefore = await File.ReadAllBytesAsync(catalogPath);
            try
            {
                await f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10)), TimeSpan.Zero, default);
                throw new InvalidOperationException("Expected gap validation failure");
            }
            catch (UsageException ex) { Equal("history_maximum_gap", ex.SafeCode); }
            var rawAfter = await File.ReadAllBytesAsync(rawPath);
            var catalogAfter = await File.ReadAllBytesAsync(catalogPath);
            True(rawBefore.SequenceEqual(rawAfter) && catalogBefore.SequenceEqual(catalogAfter));
        });
        TestFiles("history corrupt summary rejection preserves raw and catalog", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var rawPath = Path.Combine(f.Store.Root, "months", "2026-10", "2026-10-12.json");
            var catalogPath = Path.Combine(f.Store.Root, "catalog.json");
            var summaryPath = f.Store.SummaryPath(Day);
            var rawBefore = await File.ReadAllBytesAsync(rawPath);
            var catalogBefore = await File.ReadAllBytesAsync(catalogPath);
            var validSummary = await File.ReadAllTextAsync(summaryPath);
            var invalidSummary = validSummary.Replace("\"Version\": 1", "\"Version\": 99");
            await File.WriteAllTextAsync(summaryPath, invalidSummary);
            try
            {
                await f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10)), Gap, default);
                throw new InvalidOperationException("Expected summary validation failure");
            }
            catch (UsageException ex) { Equal("history_month_summary_invalid", ex.SafeCode); }
            var rawAfter = await File.ReadAllBytesAsync(rawPath);
            var catalogAfter = await File.ReadAllBytesAsync(catalogPath);
            True(rawBefore.SequenceEqual(rawAfter) && catalogBefore.SequenceEqual(catalogAfter));
            Equal(invalidSummary, await File.ReadAllTextAsync(summaryPath));
            await File.WriteAllTextAsync(summaryPath, validSummary);
            await f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10)), Gap, default);
            Equal<decimal?>(2, (await f.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
        });
        TestFiles("history later corrupt month does not write earlier cross-month shard", async f =>
        {
            var boundary = new DateTimeOffset(2026, 11, 1, 0, 0, 0, JapaneseCalendar.Offset);
            var snapshot = Snapshot(boundary.AddHours(1)) with
            {
                Window = new(WindowKind.Monthly, boundary.AddDays(-2), boundary.AddDays(40))
            };
            await f.Store.AppendAsync(Record(boundary.AddMinutes(55), boundary.AddHours(1), snapshot: snapshot), Gap, default);
            var catalogPath = Path.Combine(f.Store.Root, "catalog.json");
            var before = await File.ReadAllBytesAsync(catalogPath);
            var summaryPath = f.Store.SummaryPath(new DateOnly(2026, 11, 1));
            var summary = await File.ReadAllTextAsync(summaryPath);
            await File.WriteAllTextAsync(summaryPath, summary.Replace("\"Version\": 1", "\"Version\": 99"));
            try
            {
                await f.Store.AppendAsync(Record(boundary.AddMinutes(-5), boundary.AddMinutes(5),
                    snapshot: snapshot with { ObservedAt = boundary.AddMinutes(5) }), Gap, default);
                throw new InvalidOperationException("Expected summary validation failure");
            }
            catch (UsageException ex) { Equal("history_month_summary_invalid", ex.SafeCode); }
            var after = await File.ReadAllBytesAsync(catalogPath);
            True(before.SequenceEqual(after));
            True(!Directory.Exists(Path.GetDirectoryName(f.Store.SummaryPath(new DateOnly(2026, 10, 31)))));
        });
        TestFiles("history current month and cancelled archive never remove source", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            await Reject(() => f.Store.ArchiveAsync(Day, At, default));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            try { await f.Store.ArchiveAsync(Day, At.AddMonths(1), cancel.Token); }
            catch (OperationCanceledException) { }
            True(File.Exists(f.Store.SummaryPath(Day)));
            True(!File.Exists(f.Store.ArchivePath(Day)));
        });
        TestFiles("history corrupt archive cannot authorize raw retention", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var path = await f.Store.ArchiveAsync(Day, At.AddMonths(1), default);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await Reject(() => f.Store.VerifyArchiveAsync(Day, default));
            True(File.Exists(f.Store.SummaryPath(Day)));
        });
        TestFiles("history only verified archive IDs gate retention and preserve notification dedup", async f =>
        {
            var state = new UsageState();
            UsageEngine.Apply(state, new([Snapshot(At) with { Used = 96, UsedPercent = 96 }], []), At);
            var notices = state.Notifications.Count;
            var imported = DailyHistory.Import(state);
            foreach (var record in imported) await f.Store.AppendAsync(record, Gap, default);
            HistoryRetention.Prune(state, At.AddDays(120));
            Equal(1, state.Deltas.Count);
            await f.Store.ArchiveAsync(Day, At.AddMonths(1), default);
            HistoryRetention.Prune(state, At.AddDays(120), await f.Store.VerifyArchiveAsync(Day, default));
            Equal(0, state.Deltas.Count);
            Equal(notices, state.Notifications.Count);
            Equal(1, state.Baselines.Count);
        });
        TestFiles("history migration cancellation requires approval and leaves source unchanged", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var target = Path.Combine(f.Folder, "cancelled");
            var preview = await HistoryMigration.PreviewAsync(f.Store, target, default);
            True(!Directory.Exists(target));
            True(preview.Writable && preview.Error is null && preview.CopyBytes > 0);
            await Reject(() => HistoryMigration.ExecuteAsync(preview, false, default));
            True(!Directory.Exists(target));
            True(File.Exists(f.Store.SummaryPath(Day)));
        });
        TestFiles("history migration Japanese spaced paths copy verify and keep source", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5), 8), Gap, default);
            await f.Store.ArchiveAsync(Day, At.AddMonths(1), default);
            var preview = await HistoryMigration.PreviewAsync(f.Store, Path.Combine(f.Folder, "保存先 OneDrive"), default);
            var target = await HistoryMigration.ExecuteAsync(preview, true, default);
            Equal<decimal?>(8, (await target.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
            True((await target.VerifyArchiveAsync(Day, default)).Count > 0);
            True(File.Exists(f.Store.SummaryPath(Day)));
            Equal(0L, (await HistoryMigration.PreviewAsync(f.Store, target.Root, default)).CopyBytes);
        });
        TestFiles("history migration refuses stale preview destination conflicts and extra files", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var target = Path.Combine(f.Folder, "conflict");
            var preview = await HistoryMigration.PreviewAsync(f.Store, target, default);
            await f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10)), Gap, default);
            await Reject(() => HistoryMigration.ExecuteAsync(preview, true, default));
            await File.WriteAllTextAsync(Path.Combine(target, "history.json"), "foreign");
            await Reject(() => HistoryMigration.PreviewAsync(f.Store, target, default));
            File.Delete(Path.Combine(target, "history.json"));
            await File.WriteAllTextAsync(Path.Combine(target, "unrelated.txt"), "keep");
            await Reject(() => HistoryMigration.PreviewAsync(f.Store, target, default));
            Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "unrelated.txt")));
        });
        TestFiles("history service location switches only after verified copy and persists options", async f =>
        {
            var service = await HistoryService.LoadAsync(f.Profile, default);
            await service.RecordAsync([Record(At, At.AddMinutes(5))], Gap, default);
            True(service.Status.Saved);
            var old = service.Store.Root;
            var preview = await service.PreviewLocationAsync(Path.Combine(f.Folder, "selected"), default);
            await service.ChangeLocationAsync(preview, true, default);
            var restarted = await HistoryService.LoadAsync(f.Profile, default);
            Equal(service.Store.Root, restarted.Store.Root);
            True(Directory.Exists(old));
            var text = await File.ReadAllTextAsync(Path.Combine(service.Store.Root, "months", "2026-10", "2026-10-12.json"));
            True(!text.Contains("access_token") && !text.Contains("synthetic-history"));
        });
        TestFiles("history file opening resolves only generated paths and reports missing archive", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var folder = await HistoryLocations.ResolveAsync(f.Store, HistoryLocation.Folder, null, default);
            Equal(f.Store.Root, folder.Path);
            Equal(false, folder.SelectFile);
            var summary = await HistoryLocations.ResolveAsync(f.Store, HistoryLocation.SummaryFile, Day, default);
            Equal(f.Store.SummaryPath(Day), summary.Path);
            Equal(true, summary.SelectFile);
            await Reject(() => HistoryLocations.ResolveAsync(f.Store, HistoryLocation.ArchiveFile, Day, default));
            await Reject(() => HistoryLocations.ResolveAsync(f.Store, (HistoryLocation)99, Day, default));
            await f.Store.ArchiveAsync(Day, At.AddMonths(1), default);
            Equal(f.Store.ArchivePath(Day), (await HistoryLocations.ResolveAsync(
                f.Store, HistoryLocation.ArchiveFile, Day, default)).Path);
        });
        TestFiles("history commands require server-held migration preview ID", async f =>
        {
            var service = await HistoryService.LoadAsync(f.Profile, default);
            await service.RecordAsync([Record(At, At.AddMinutes(5))], Gap, default);
            var commands = new HistoryCommands(service);
            var overview = await commands.ExecuteAsync(new(HistoryAction.Overview, From: Day, Through: Day), default);
            True(overview.Success && overview.Options is not null && overview.Status is not null);
            Equal<decimal?>(1, overview.Overview!.Items.Single().Observed);
            var dayPage = await commands.ExecuteAsync(new(HistoryAction.QueryPage, Query: new(Day, Day, Key)), default);
            True(dayPage.Success);
            Equal<decimal?>(1, dayPage.DaysPage!.Days.Single().Slices.Single().Observed);
            var rejected = await commands.ExecuteAsync(new(HistoryAction.ChangeLocation, PreviewId: "forged"), default);
            True(!rejected.Success);
            var preview = await commands.ExecuteAsync(new(HistoryAction.PreviewLocation,
                Folder: Path.Combine(f.Folder, "selection")), default);
            True(preview.Success && preview.PreviewId is not null);
            var applied = await commands.ExecuteAsync(new(HistoryAction.ChangeLocation, PreviewId: preview.PreviewId), default);
            True(applied.Success);
            True(!(await commands.ExecuteAsync(new(HistoryAction.ChangeLocation, PreviewId: preview.PreviewId), default)).Success);
            using var frame = new MemoryStream();
            await PipeProtocol.WriteAsync(frame, new HistoryResponse(true,
                Catalog: (await service.Store.CatalogAsync(default)).Items,
                Days: (await service.Store.QueryAsync(new(Day, Day, Key), default)).ToList()), default);
            True(frame.Length < PipeProtocol.MaximumMessageBytes);
            frame.Position = 0;
            Equal(1, (await PipeProtocol.ReadAsync<HistoryResponse>(frame, default)).Days!.Count);
        });
        TestFiles("history unavailable existing folder is not silently initialized empty", async f =>
        {
            var service = await HistoryService.LoadAsync(f.Profile, default);
            await service.RecordAsync([Record(At, At.AddMinutes(5))], Gap, default);
            var manifest = Path.Combine(service.Store.Root, "history.json");
            var text = await File.ReadAllTextAsync(manifest);
            File.Delete(manifest);
            await service.RecordAsync([Record(At.AddMinutes(5), At.AddMinutes(10))], Gap, default);
            True(!service.Status.Saved && service.Status.Error is not null);
            Equal(1, service.Status.PendingRecords);
            True(!File.Exists(manifest));
            await File.WriteAllTextAsync(manifest, text);
            await service.RetryAsync(Gap, default);
            True(service.Status.Saved);
            Equal<decimal?>(2, (await service.Store.QueryAsync(new(Day, Day, Key), default)).Single().Slices.Single().Observed);
        });
        TestFiles("history migration rejects fabricated low-space preview without writes", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var preview = await HistoryMigration.PreviewAsync(f.Store, Path.Combine(f.Folder, "space"), default);
            await Reject(() => HistoryMigration.ExecuteAsync(preview with
                { AvailableBytes = 0, Error = "history_migration_insufficient_space" }, true, default));
            True(!Directory.Exists(preview.Destination));
        });
        TestFiles("history catalog retains old accounts without scanning archives", async f =>
        {
            await f.Store.AppendAsync(Record(At, At.AddMinutes(5)), Gap, default);
            var other = Snapshot(At.AddMinutes(10)) with
                { Key = new(AccountKey.FromIdentity(Provider.Copilot, "other-synthetic"), "monthly") };
            await f.Store.AppendAsync(Record(At.AddMinutes(5), other.ObservedAt, snapshot: other), Gap, default);
            var catalog = await f.Store.CatalogAsync(default);
            Equal(2, catalog.Items.Count);
            True(catalog.Items.Any(i => i.Key == Key));
        });
    }

    private sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "history-test-" + Guid.NewGuid().ToString("N"));
        public string Writer { get; } = Guid.NewGuid().ToString("N");
        public string Profile => Path.Combine(Folder, "profile");
        public HistoryStore Store { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Folder);
            Store = new(Path.Combine(Folder, "history"), Writer);
        }
        public void Dispose() => Directory.Delete(Folder, recursive: true);
    }
}
