namespace AiUsageTray.Core;

public sealed record HistoryReceiptMonth(int Version, string WriterId, string Month, List<string> Ids);

public sealed class HistoryJournal
{
    private readonly string folder;
    private readonly string writerId;

    public HistoryJournal(string localProfile, string writerId)
    {
        this.writerId = Guid.TryParseExact(writerId, "N", out _) ? writerId
            : throw new UsageException(FailureKind.Storage, "history_writer_id_invalid");
        folder = Path.Combine(Path.GetFullPath(localProfile), "history-journal", writerId);
    }

    private string ReceiptPath(string month) => Path.Combine(folder, "receipts", month + ".json");
    private string PendingFolder => Path.Combine(folder, "pending");

    private async Task<HistoryReceiptMonth> ReceiptsAsync(string month, CancellationToken cancellationToken)
    {
        var path = ReceiptPath(month);
        if (!File.Exists(path)) return new(1, writerId, month, []);
        var receipts = await HistoryFiles.ReadAsync<HistoryReceiptMonth>(path, cancellationToken);
        if (receipts.Version != 1 || receipts.WriterId != writerId || receipts.Month != month ||
            receipts.Ids is null || receipts.Ids.Any(id => id is null || id.Length != 64 || !id.All(Uri.IsHexDigit)) ||
            receipts.Ids.Distinct().Count() != receipts.Ids.Count)
            throw new UsageException(FailureKind.Storage, "history_receipts_invalid");
        return receipts;
    }

    public async Task EnqueueAsync(IEnumerable<HistoryRecord> source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PendingFolder);
        foreach (var group in source.GroupBy(r => DailyHistory.Month(JapaneseCalendar.Date(r.Delta.To))))
        {
            var receipts = await ReceiptsAsync(group.Key, cancellationToken);
            var saved = receipts.Ids.ToHashSet();
            foreach (var record in group)
            {
                record.Validate();
                if (saved.Contains(record.Id)) continue;
                var path = Path.Combine(PendingFolder, record.Id + ".json");
                if (File.Exists(path))
                {
                    var previous = await HistoryFiles.ReadAsync<HistoryRecord>(path, cancellationToken);
                    previous.Validate();
                    if (previous.Id != record.Id)
                        throw new UsageException(FailureKind.Storage, "history_pending_id_mismatch");
                    continue;
                }
                await HistoryFiles.WriteAsync(path, record, cancellationToken);
            }
        }
    }

    public int PendingCount => Directory.Exists(PendingFolder)
        ? Directory.EnumerateFiles(PendingFolder, "*.json").Count() : 0;

    public async Task<HistoryStorageStatus> FlushAsync(
        HistoryStore store, TimeSpan maximumGap, CancellationToken cancellationToken)
    {
        if (store.WriterId != writerId)
            throw new UsageException(FailureKind.Storage, "history_journal_writer_mismatch");
        Directory.CreateDirectory(PendingFolder);
        try
        {
            // Fixed-size batches keep resident memory bounded even after a long outage.
            foreach (var path in Directory.EnumerateFiles(PendingFolder, "*.json").Take(512))
            {
                var record = await HistoryFiles.ReadAsync<HistoryRecord>(path, cancellationToken);
                record.Validate();
                if (Path.GetFileNameWithoutExtension(path) != record.Id)
                    throw new UsageException(FailureKind.Storage, "history_pending_id_mismatch");
                await store.AppendAsync(record, maximumGap, cancellationToken);
                var month = DailyHistory.Month(JapaneseCalendar.Date(record.Delta.To));
                var receipts = await ReceiptsAsync(month, cancellationToken);
                if (!receipts.Ids.Contains(record.Id))
                {
                    receipts.Ids.Add(record.Id);
                    await HistoryFiles.WriteAsync(ReceiptPath(month), receipts, cancellationToken);
                }
                // Only acknowledged queue entries are removed; history and archive sources are kept.
                File.Delete(path);
            }
            return new(PendingCount, null);
        }
        catch (UsageException ex) when (ex.Kind == FailureKind.Storage)
        {
            return new(PendingCount, ex.SafeCode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(PendingCount, "history_storage_unavailable:" + ex.GetType().Name);
        }
    }
}
