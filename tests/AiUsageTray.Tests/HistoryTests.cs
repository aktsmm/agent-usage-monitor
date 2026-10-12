using System.Text.Json;
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
            Equal(Coverage.CompleteToObservation, result.Coverage);
            Equal<decimal?>(100m / 31, result.Guidance);
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
            await f.Store.AppendAsync(Record(end.AddMinutes(-5), end.AddMinutes(5), 10, snapshot: s), Gap, default);
            var days = await f.Store.QueryAsync(new(new(2026, 10, 31), new(2026, 11, 1), Key), default);
            Equal(2, days.Count);
            True(days.All(d => d.Slices.Single().UnallocatedBoundary));
            True(File.Exists(f.Store.SummaryPath(new(2026, 10, 31))) && File.Exists(f.Store.SummaryPath(new(2026, 11, 1))));
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
            await Reject(() => f.Store.AppendAsync(Record(At.AddMinutes(5), At.AddMinutes(10)), Gap, default));
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
