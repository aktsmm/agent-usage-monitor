using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiUsageTray.Core;

public sealed class HistoryStore
{
    public string Root { get; }
    public string WriterId { get; }
    public string ArchiveFolder => Resolve("archives");
    private bool requireExisting;

    public HistoryStore(string root, string writerId, bool requireExisting = false)
    {
        Root = Path.GetFullPath(root);
        WriterId = Guid.TryParseExact(writerId, "N", out _) ? writerId
            : throw new UsageException(FailureKind.Storage, "history_writer_id_invalid");
        if (Path.GetPathRoot(Root) == Root)
            throw new UsageException(FailureKind.Storage, "history_root_is_volume_root");
        this.requireExisting = requireExisting;
    }

    internal async Task<FileStream> AcquireAsync(CancellationToken cancellationToken)
    {
        HistoryFiles.RejectLinks(Root);
        if (requireExisting && !File.Exists(Path.Combine(Root, "history.json")))
            throw new UsageException(FailureKind.Storage, "history_existing_folder_unavailable");
        Directory.CreateDirectory(Root);
        var lease = new FileStream(Path.Combine(Root, ".writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = Path.Combine(Root, "history.json");
            if (!File.Exists(path))
            {
                if (Directory.EnumerateFileSystemEntries(Root).Any(p => Path.GetFileName(p) != ".writer.lock"))
                    throw new UsageException(FailureKind.Storage, "history_folder_not_empty_or_owned");
                await HistoryFiles.WriteAsync(path, new HistoryManifest(1, WriterId, "Asia/Tokyo",
                    "Observed quota deltas; not money or tokens"), cancellationToken);
            }
            await ValidateOwnershipAsync(cancellationToken);
            requireExisting = true;
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task ValidateOwnershipAsync(CancellationToken cancellationToken)
    {
        var manifest = await ReadAsync<HistoryManifest>("history.json", cancellationToken);
        if (manifest.Version != 1 || manifest.WriterId != WriterId || manifest.Calendar != "Asia/Tokyo" ||
            manifest.Measurement != "Observed quota deltas; not money or tokens")
            throw new UsageException(FailureKind.Storage, "history_version_or_writer_mismatch");
    }

    internal string Resolve(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.Storage, "history_path_outside_root");
        HistoryFiles.RejectLinks(path);
        return path;
    }

    internal Task<T> ReadAsync<T>(string relative, CancellationToken cancellationToken) =>
        HistoryFiles.ReadAsync<T>(Resolve(relative), cancellationToken);

    public string SummaryPath(DateOnly date) =>
        Resolve(Path.Combine("months", DailyHistory.Month(date), "summary.json"));

    private string RawRelative(DateOnly date) => Path.Combine("months", DailyHistory.Month(date),
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    private async Task<HistoryRawDay> LoadDayAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var path = Resolve(RawRelative(date));
        if (!File.Exists(path)) return new(1, WriterId, date, []);
        var day = await HistoryFiles.ReadAsync<HistoryRawDay>(path, cancellationToken);
        if (day.Version != 1 || day.WriterId != WriterId || day.Date != date || day.Records is null ||
            day.Records.Any(r => r is null) ||
            day.Records.Select(r => r.Id).Distinct().Count() != day.Records.Count)
            throw new UsageException(FailureKind.Storage, "history_raw_day_invalid");
        foreach (var record in day.Records)
        {
            record.Validate();
            if (!record.AffectedDays().Contains(date))
                throw new UsageException(FailureKind.Storage, "history_raw_day_record_mismatch");
        }
        return day;
    }

    private async Task<HistoryMonth> LoadMonthAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var path = SummaryPath(date);
        if (!File.Exists(path))
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory) &&
                Directory.EnumerateFiles(directory, "*.json").Any())
                throw new UsageException(FailureKind.Storage, "history_month_summary_missing");
            return new(1, WriterId, DailyHistory.Month(date), []);
        }
        var month = await HistoryFiles.ReadAsync<HistoryMonth>(path, cancellationToken);
        if (month.Version != 1 || month.WriterId != WriterId || month.Month != DailyHistory.Month(date) ||
            month.Slices is null || month.Slices.Any(s => s is null || DailyHistory.Month(s.Date) != month.Month ||
                s.Key is null || s.Key.Account is null || s.PeriodId is null || s.Events is null ||
                string.IsNullOrWhiteSpace(s.Key.Account.Id) || s.Key.Account.Id.Length != 64 ||
                !s.Key.Account.Id.All(Uri.IsHexDigit) || !Enum.IsDefined(s.Key.Account.Provider) ||
                string.IsNullOrWhiteSpace(s.Key.Bucket) || s.Window is null ||
                !Enum.IsDefined(s.Window.Kind) || s.Events.Any(e => !Enum.IsDefined(e)) ||
                !Enum.IsDefined(s.Coverage) || !Enum.IsDefined(s.Unit) ||
                s.Observed < 0 || s.Guidance < 0 ||
                (s.Coverage == Coverage.CompleteToObservation && s.ObservedThrough is null) ||
                (s.Coverage == Coverage.Unmeasured ? s.Observed is not null : s.Observed is null)) ||
            month.Slices.Select(s => (s.Date, s.Key, s.PeriodId)).Distinct().Count() != month.Slices.Count)
            throw new UsageException(FailureKind.Storage, "history_month_summary_invalid");
        return month;
    }

    public async Task AppendAsync(HistoryRecord record, TimeSpan maximumGap, CancellationToken cancellationToken)
    {
        record.Validate();
        await using var lease = await AcquireAsync(cancellationToken);
        var catalog = await CatalogAsync(cancellationToken);
        var previous = catalog.Items.Find(i => i.Key == record.Snapshot.Key);
        catalog.Items.RemoveAll(i => i.Key == record.Snapshot.Key);
        var at = record.Snapshot.ObservedAt;
        catalog.Items.Add(new(record.Snapshot.Key,
            previous is not null && previous.FirstObservation < at ? previous.FirstObservation : at,
            previous is not null && previous.LastObservation > at ? previous.LastObservation : at));
        await HistoryFiles.WriteAsync(Resolve("catalog.json"), catalog, cancellationToken);
        foreach (var date in record.AffectedDays())
        {
            var day = await LoadDayAsync(date, cancellationToken);
            if (day.Records.All(r => r.Id != record.Id))
            {
                var archive = ArchivePath(date);
                if (File.Exists(archive))
                    throw new UsageException(FailureKind.Storage, "history_archived_month_is_sealed");
                day.Records.Add(record);
                await HistoryFiles.WriteAsync(Resolve(RawRelative(date)), day, cancellationToken);
            }
            // Replay also repairs a crash between raw persistence and summary replacement.
            var slices = DailyHistory.Project(date, day.Records, maximumGap);
            var month = File.Exists(SummaryPath(date))
                ? await LoadMonthAsync(date, cancellationToken)
                : await RebuildMonthAsync(date, maximumGap, cancellationToken);
            month.Slices.RemoveAll(s => s.Date == date);
            month.Slices.AddRange(slices);
            month.Slices.Sort((a, b) => a.Date.CompareTo(b.Date));
            await HistoryFiles.WriteAsync(SummaryPath(date), month, cancellationToken);
        }
    }

    private async Task<HistoryMonth> RebuildMonthAsync(DateOnly month, TimeSpan maximumGap,
        CancellationToken cancellationToken)
    {
        var data = new HistoryMonth(1, WriterId, DailyHistory.Month(month), []);
        var directory = Path.GetDirectoryName(SummaryPath(month))!;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                DailyHistory.Month(date) != data.Month)
                throw new UsageException(FailureKind.Storage, "history_month_unknown_file");
            var day = await LoadDayAsync(date, cancellationToken);
            data.Slices.AddRange(DailyHistory.Project(date, day.Records, maximumGap));
        }
        return data;
    }

    public async Task<HistoryCatalog> CatalogAsync(CancellationToken cancellationToken)
    {
        await ValidateOwnershipAsync(cancellationToken);
        var path = Resolve("catalog.json");
        if (!File.Exists(path))
        {
            var months = Resolve("months");
            if (Directory.Exists(months) && Directory.EnumerateDirectories(months).Any())
                throw new UsageException(FailureKind.Storage, "history_catalog_missing");
            return new(1, WriterId, []);
        }
        var catalog = await HistoryFiles.ReadAsync<HistoryCatalog>(path, cancellationToken);
        if (catalog.Version != 1 || catalog.WriterId != WriterId || catalog.Items is null ||
            catalog.Items.Any(i => i is null) ||
            catalog.Items.Select(i => i.Key).Distinct().Count() != catalog.Items.Count ||
            catalog.Items.Any(i => i.Key is null || i.Key.Account is null ||
                i.Key.Account.Id is null || i.Key.Account.Id.Length != 64 ||
                !i.Key.Account.Id.All(Uri.IsHexDigit) || i.FirstObservation > i.LastObservation ||
                string.IsNullOrWhiteSpace(i.Key.Bucket) || !Enum.IsDefined(i.Key.Account.Provider)))
            throw new UsageException(FailureKind.Storage, "history_catalog_invalid");
        return catalog;
    }

    public async Task<IReadOnlyList<HistoryDay>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        query.Validate();
        await ValidateOwnershipAsync(cancellationToken);
        var slices = new List<DailyHistorySlice>();
        for (var month = new DateOnly(query.From.Year, query.From.Month, 1);
            month <= query.Through; month = month.AddMonths(1))
        {
            var data = await LoadMonthAsync(month, cancellationToken);
            slices.AddRange(data.Slices.Where(s => s.Key == query.Key &&
                s.Date >= query.From && s.Date <= query.Through));
        }
        return Enumerable.Range(0, query.Through.DayNumber - query.From.DayNumber + 1)
            .Select(i => query.From.AddDays(i))
            .Select(d => new HistoryDay(d, slices.Where(s => s.Date == d).ToArray())).ToArray();
    }

    public async Task ValidateContentAsync(CancellationToken cancellationToken)
    {
        await ValidateOwnershipAsync(cancellationToken);
        await CatalogAsync(cancellationToken);
        var months = Resolve("months");
        if (Directory.Exists(months))
        {
            foreach (var folder in Directory.EnumerateDirectories(months))
            {
                if (!DateOnly.TryParseExact(Path.GetFileName(folder) + "-01", "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
                    throw new UsageException(FailureKind.Storage, "history_month_folder_invalid");
                HistoryFiles.RejectLinks(folder);
                if (!File.Exists(SummaryPath(month)))
                    throw new UsageException(FailureKind.Storage, "history_month_summary_missing");
                await LoadMonthAsync(month, cancellationToken);
                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    if (Path.GetFileName(path) == "summary.json") continue;
                    if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                        Path.GetExtension(path) != ".json" || DailyHistory.Month(date) != DailyHistory.Month(month))
                        throw new UsageException(FailureKind.Storage, "history_month_unknown_file");
                    await LoadDayAsync(date, cancellationToken);
                }
            }
        }
        if (Directory.Exists(ArchiveFolder))
        {
            foreach (var path in Directory.EnumerateFiles(ArchiveFolder))
            {
                if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path) + "-01", "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) ||
                    Path.GetExtension(path) != ".zip")
                    throw new UsageException(FailureKind.Storage, "history_archive_unknown_file");
                await VerifyArchiveAsync(month, cancellationToken);
            }
        }
    }

    public string ArchivePath(DateOnly date) =>
        Resolve(Path.Combine("archives", DailyHistory.Month(date) + ".zip"));

    public async Task<string> ArchiveAsync(DateOnly month, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (new DateOnly(month.Year, month.Month, 1).AddMonths(1) >
            JapaneseCalendar.Date(now))
            throw new UsageException(FailureKind.Storage, "history_current_month_cannot_archive");
        await using var lease = await AcquireAsync(cancellationToken);
        var path = ArchivePath(month);
        if (File.Exists(path))
        {
            await VerifyArchiveAsync(month, cancellationToken);
            return path;
        }
        var folder = Resolve(Path.Combine("months", DailyHistory.Month(month)));
        if (!Directory.Exists(folder))
            throw new UsageException(FailureKind.Storage, "history_month_not_recorded");
        await LoadMonthAsync(month, cancellationToken);
        var files = Directory.EnumerateFiles(folder, "*.json").Order().ToArray();
        if (files.All(p => Path.GetFileName(p) == "summary.json"))
            throw new UsageException(FailureKind.Storage, "history_month_raw_missing");
        var entries = new List<HistoryArchiveEntry>();
        foreach (var file in files.Where(p => Path.GetFileName(p) != "summary.json"))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                DailyHistory.Month(date) != DailyHistory.Month(month))
                throw new UsageException(FailureKind.Storage, "history_month_unknown_file");
            await LoadDayAsync(date, cancellationToken);
            entries.Add(new(Path.GetFileName(file), new FileInfo(file).Length,
                await HistoryFiles.HashAsync(file, cancellationToken)));
        }
        var manifest = new HistoryArchiveManifest(1, WriterId, DailyHistory.Month(month), entries);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(ArchiveFolder);
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(Path.Combine(folder, entry.Name), entry.Name, CompressionLevel.Optimal);
                }
                await using var stream = zip.CreateEntry("manifest.json").Open();
                await JsonSerializer.SerializeAsync(stream, manifest, StateStore.JsonOptions, cancellationToken);
            }
            await VerifyArchiveFileAsync(temporary, month, cancellationToken);
            using (var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                file.Flush(true);
            File.Move(temporary, path, overwrite: false);
            return path;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<IReadOnlySet<string>> VerifyArchiveAsync(DateOnly month, CancellationToken cancellationToken)
    {
        await ValidateOwnershipAsync(cancellationToken);
        return await VerifyArchiveFileAsync(ArchivePath(month), month, cancellationToken);
    }

    private async Task<IReadOnlySet<string>> VerifyArchiveFileAsync(
        string path, DateOnly month, CancellationToken cancellationToken)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null || manifestEntry.Length > HistoryFiles.MaximumBytes)
                throw new UsageException(FailureKind.Storage, "history_archive_manifest_missing");
            await using var manifestStream = manifestEntry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<HistoryArchiveManifest>(
                manifestStream, StateStore.JsonOptions, cancellationToken);
            if (manifest is null || manifest.Version != 1 || manifest.WriterId != WriterId ||
                manifest.Month != DailyHistory.Month(month) || manifest.Entries is null ||
                manifest.Entries.Count is < 1 or > 31 ||
                manifest.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Name)) ||
                manifest.Entries.Select(e => e.Name).Distinct().Count() != manifest.Entries.Count ||
                zip.Entries.Count != manifest.Entries.Count + 1)
                throw new UsageException(FailureKind.Storage, "history_archive_manifest_invalid");
            var ids = new HashSet<string>();
            foreach (var declared in manifest.Entries)
            {
                if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(declared.Name), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    declared.Name != date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json" ||
                    DailyHistory.Month(date) != manifest.Month)
                    throw new UsageException(FailureKind.Storage, "history_archive_entry_path_invalid");
                var entry = zip.GetEntry(declared.Name);
                if (entry is null || declared.Bytes is < 1 or > HistoryFiles.MaximumBytes ||
                    entry.Length != declared.Bytes)
                    throw new UsageException(FailureKind.Storage, "history_archive_entry_size_invalid");
                await using var stream = entry.Open();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken);
                if (Convert.ToHexString(SHA256.HashData(memory.GetBuffer().AsSpan(0, (int)memory.Length))) != declared.Hash)
                    throw new UsageException(FailureKind.Storage, "history_archive_hash_mismatch");
                memory.Position = 0;
                var day = await JsonSerializer.DeserializeAsync<HistoryRawDay>(
                    memory, StateStore.JsonOptions, cancellationToken);
                if (day is null || day.Version != 1 || day.WriterId != WriterId || day.Date != date ||
                    day.Records is null || day.Records.Count == 0 || day.Records.Any(r => r is null) ||
                    day.Records.Select(r => r.Id).Distinct().Count() != day.Records.Count)
                    throw new UsageException(FailureKind.Storage, "history_archive_day_invalid");
                foreach (var record in day.Records)
                {
                    record.Validate();
                    if (!record.AffectedDays().Contains(date))
                        throw new UsageException(FailureKind.Storage, "history_archive_record_day_invalid");
                    // A cross-day delta is eligible for retention only in its observation month.
                    if (DailyHistory.Month(JapaneseCalendar.Date(record.Delta.To)) == manifest.Month)
                        ids.Add(record.Id);
                }
            }
            return ids;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new UsageException(FailureKind.Storage, "history_archive_corrupt");
        }
    }
}
