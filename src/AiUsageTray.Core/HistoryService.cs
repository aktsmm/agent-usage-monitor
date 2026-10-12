namespace AiUsageTray.Core;

public sealed record HistoryOptions(int Version, string WriterId, string Folder)
{
    public void Validate()
    {
        if (Version != 1 || !Guid.TryParseExact(WriterId, "N", out _) ||
            string.IsNullOrWhiteSpace(Folder) || !Path.IsPathFullyQualified(Folder))
            throw new UsageException(FailureKind.Storage, "history_options_invalid");
    }
}

public sealed class HistoryService
{
    private readonly string optionsPath;
    private readonly HistoryJournal journal;
    public HistoryOptions Options { get; private set; }
    public HistoryStore Store { get; private set; }
    public HistoryStorageStatus Status { get; private set; }

    private HistoryService(string localProfile, HistoryOptions options)
    {
        optionsPath = Path.Combine(localProfile, "history-settings.json");
        Options = options;
        Store = new(options.Folder, options.WriterId, requireExisting: true);
        journal = new(localProfile, options.WriterId);
        Status = new(journal.PendingCount, null);
    }

    public static async Task<HistoryService> LoadAsync(string localProfile, CancellationToken cancellationToken)
    {
        localProfile = Path.GetFullPath(localProfile);
        var path = Path.Combine(localProfile, "history-settings.json");
        HistoryOptions options;
        if (File.Exists(path))
        {
            options = await HistoryFiles.ReadAsync<HistoryOptions>(path, cancellationToken);
            options.Validate();
        }
        else
        {
            var writer = Guid.NewGuid().ToString("N");
            options = new(1, writer, Path.Combine(localProfile, "history", writer));
            var store = new HistoryStore(options.Folder, options.WriterId);
            await using var lease = await store.AcquireAsync(cancellationToken);
            await HistoryFiles.WriteAsync(path, options, cancellationToken);
        }
        return new(localProfile, options);
    }

    public async Task RecordAsync(IEnumerable<HistoryRecord> records, TimeSpan maximumGap,
        CancellationToken cancellationToken)
    {
        try
        {
            await journal.EnqueueAsync(records, cancellationToken);
        }
        catch (Exception ex) when (ex is UsageException or IOException or UnauthorizedAccessException)
        {
            Status = new(journal.PendingCount, ex is UsageException usage ? usage.SafeCode
                : "history_local_journal_unavailable:" + ex.GetType().Name);
            throw;
        }
        await RetryAsync(maximumGap, cancellationToken);
    }

    public async Task RetryAsync(TimeSpan maximumGap, CancellationToken cancellationToken)
    {
        Status = await journal.FlushAsync(Store, maximumGap, cancellationToken);
    }

    public Task<string> ArchiveAsync(DateOnly month, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (journal.PendingCount != 0)
            throw new UsageException(FailureKind.Storage, "history_archive_pending_records");
        return Store.ArchiveAsync(month, now, cancellationToken);
    }

    public Task<HistoryMigrationPreview> PreviewLocationAsync(string selectedFolder, CancellationToken cancellationToken)
    {
        // The picker may select any writable local/OneDrive parent; only this app-owned namespace is copied.
        var destination = Path.Combine(Path.GetFullPath(selectedFolder), "AgentUsageHistory", Options.WriterId);
        return HistoryMigration.PreviewAsync(Store, destination, cancellationToken);
    }

    public async Task ChangeLocationAsync(HistoryMigrationPreview preview, bool userApproved,
        CancellationToken cancellationToken)
    {
        if (preview.Source != Store.Root || preview.WriterId != Options.WriterId)
            throw new UsageException(FailureKind.Storage, "history_migration_wrong_source");
        var target = await HistoryMigration.ExecuteAsync(preview, userApproved, cancellationToken);
        var updated = Options with { Folder = target.Root };
        await HistoryFiles.WriteAsync(optionsPath, updated, cancellationToken);
        Options = updated;
        Store = target;
        Status = new(journal.PendingCount, null);
    }
}
