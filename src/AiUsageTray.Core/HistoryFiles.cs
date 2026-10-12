using System.Security.Cryptography;
using System.Text.Json;

namespace AiUsageTray.Core;

internal static class HistoryFiles
{
    public const int MaximumBytes = 8 * 1024 * 1024;

    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumBytes)
            throw new UsageException(FailureKind.Storage, "history_file_too_large");
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(file, StateStore.JsonOptions, cancellationToken)
                ?? throw new UsageException(FailureKind.Storage, "history_file_empty");
        }
        catch (JsonException)
        {
            throw new UsageException(FailureKind.Storage, "history_json_invalid");
        }
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, StateStore.JsonOptions);
        if (bytes.Length > MaximumBytes)
            throw new UsageException(FailureKind.Storage, "history_file_too_large");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
    }

    public static void RejectLinks(string path)
    {
        for (var part = new DirectoryInfo(Path.GetFullPath(path)); part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UsageException(FailureKind.Storage, "history_reparse_point_not_supported");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UsageException(FailureKind.Storage, "history_reparse_point_not_supported");
    }
}

public sealed record HistoryManifest(int Version, string WriterId, string Calendar, string Measurement);
public sealed record HistoryCatalogItem(QuotaKey Key, DateTimeOffset FirstObservation, DateTimeOffset LastObservation);
public sealed record HistoryCatalog(int Version, string WriterId, List<HistoryCatalogItem> Items);
public sealed record HistoryRawDay(int Version, string WriterId, DateOnly Date, List<HistoryRecord> Records);
public sealed record HistoryMonth(int Version, string WriterId, string Month, List<DailyHistorySlice> Slices);
public sealed record HistoryArchiveEntry(string Name, long Bytes, string Hash);
public sealed record HistoryArchiveManifest(int Version, string WriterId, string Month,
    List<HistoryArchiveEntry> Entries);
public sealed record HistoryStorageStatus(int PendingRecords, string? Error)
{
    public bool Saved => PendingRecords == 0 && Error is null;
}
