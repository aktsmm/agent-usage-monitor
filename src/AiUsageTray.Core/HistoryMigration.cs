namespace AiUsageTray.Core;

public sealed record HistoryCopyFile(string RelativePath, long Bytes, string Hash, bool AlreadyPresent);
public sealed record HistoryMigrationPreview(string Source, string Destination, string WriterId,
    List<HistoryCopyFile> Files, long CopyBytes, long? AvailableBytes, bool Writable, string? Error);

public static class HistoryMigration
{
    public static async Task<HistoryMigrationPreview> PreviewAsync(
        HistoryStore source, string destination, CancellationToken cancellationToken)
    {
        var target = new HistoryStore(destination, source.WriterId);
        var prefix = Path.DirectorySeparatorChar.ToString();
        if (string.Equals(source.Root, target.Root, StringComparison.OrdinalIgnoreCase) ||
            (target.Root + prefix).StartsWith(source.Root + prefix, StringComparison.OrdinalIgnoreCase) ||
            (source.Root + prefix).StartsWith(target.Root + prefix, StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.Storage, "history_migration_overlapping_folders");
        await using var lease = await source.AcquireAsync(cancellationToken);
        await source.ValidateContentAsync(cancellationToken);
        HistoryFiles.RejectLinks(target.Root);
        var files = new List<HistoryCopyFile>();
        foreach (var file in OwnedFiles(source))
        {
            var relative = Path.GetRelativePath(source.Root, file);
            var hash = await HistoryFiles.HashAsync(file, cancellationToken);
            var targetPath = target.Resolve(relative);
            bool exists = File.Exists(targetPath);
            if (exists && await HistoryFiles.HashAsync(targetPath, cancellationToken) != hash)
                throw new UsageException(FailureKind.Storage, "history_migration_destination_conflict");
            files.Add(new(relative, new FileInfo(file).Length, hash, exists));
        }
        if (Directory.Exists(target.Root))
        {
            var expected = files.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in OwnedFiles(target))
                if (!expected.Contains(Path.GetRelativePath(target.Root, path)))
                    throw new UsageException(FailureKind.Storage, "history_migration_destination_extra_files");
        }
        long? available = null;
        bool writable = false;
        string? error = null;
        var probe = target.Resolve(".probe-" + Guid.NewGuid().ToString("N"));
        bool created = !Directory.Exists(target.Root);
        try
        {
            Directory.CreateDirectory(target.Root);
            await File.WriteAllTextAsync(probe, "probe", cancellationToken);
            writable = true;
            var drive = DriveInfo.GetDrives().FirstOrDefault(d =>
                string.Equals(d.Name, Path.GetPathRoot(target.Root), StringComparison.OrdinalIgnoreCase));
            if (drive is { IsReady: true }) available = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "history_migration_probe_failed:" + ex.GetType().Name;
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
            if (created && Directory.Exists(target.Root) && !Directory.EnumerateFileSystemEntries(target.Root).Any())
                Directory.Delete(target.Root, recursive: false);
        }
        long bytes = files.Where(f => !f.AlreadyPresent).Sum(f => f.Bytes);
        if (available < bytes) error = "history_migration_insufficient_space";
        return new(source.Root, target.Root, source.WriterId, files, bytes, available, writable, error);
    }

    private static IEnumerable<string> OwnedFiles(HistoryStore store)
    {
        var queue = new Queue<string>();
        queue.Enqueue(store.Root);
        while (queue.TryDequeue(out var directory))
        {
            HistoryFiles.RejectLinks(directory);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                HistoryFiles.RejectLinks(path);
                if (Path.GetFileName(path) == ".writer.lock" && directory == store.Root) continue;
                var relative = Path.GetRelativePath(store.Root, path);
                if (relative != "history.json" && relative != "catalog.json" &&
                    !(relative.StartsWith("months" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                      Path.GetExtension(path) == ".json") &&
                    !(relative.StartsWith("archives" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                      Path.GetExtension(path) == ".zip"))
                    throw new UsageException(FailureKind.Storage, "history_migration_unknown_file");
                yield return path;
            }
            foreach (var child in Directory.EnumerateDirectories(directory)) queue.Enqueue(child);
        }
    }

    public static async Task<HistoryStore> ExecuteAsync(
        HistoryMigrationPreview preview, bool userApproved, CancellationToken cancellationToken)
    {
        if (!userApproved)
            throw new UsageException(FailureKind.Forbidden, "history_migration_confirmation_required");
        if (!preview.Writable || preview.Error is not null)
            throw new UsageException(FailureKind.Storage, "history_migration_preview_not_ready");
        var source = new HistoryStore(preview.Source, preview.WriterId, requireExisting: true);
        var target = new HistoryStore(preview.Destination, preview.WriterId, requireExisting: true);
        await using var sourceLease = await source.AcquireAsync(cancellationToken);
        Directory.CreateDirectory(target.Root);
        HistoryFiles.RejectLinks(target.Root);
        await using var targetLease = new FileStream(target.Resolve(".writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var actual = OwnedFiles(source).Select(p => Path.GetRelativePath(source.Root, p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(preview.Files.Select(f => f.RelativePath)))
            throw new UsageException(FailureKind.Storage, "history_migration_preview_stale");
        var expected = preview.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (OwnedFiles(target).Any(p => !expected.Contains(Path.GetRelativePath(target.Root, p))))
            throw new UsageException(FailureKind.Storage, "history_migration_destination_changed");
        // Revalidate the complete source and destination before copying any persistent files.
        foreach (var file in preview.Files)
        {
            if (await HistoryFiles.HashAsync(source.Resolve(file.RelativePath), cancellationToken) != file.Hash)
                throw new UsageException(FailureKind.Storage, "history_migration_preview_stale");
            var path = target.Resolve(file.RelativePath);
            if (File.Exists(path) && await HistoryFiles.HashAsync(path, cancellationToken) != file.Hash)
                throw new UsageException(FailureKind.Storage, "history_migration_destination_conflict");
        }
        foreach (var file in preview.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var from = source.Resolve(file.RelativePath);
            var to = target.Resolve(file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (!File.Exists(to))
            {
                var temporary = to + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        4096, FileOptions.WriteThrough))
                    {
                        await input.CopyToAsync(output, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                        output.Flush(true);
                    }
                    if (await HistoryFiles.HashAsync(temporary, cancellationToken) != file.Hash)
                        throw new UsageException(FailureKind.Storage, "history_migration_verify_failed");
                    File.Move(temporary, to, overwrite: false);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            if (await HistoryFiles.HashAsync(to, cancellationToken) != file.Hash)
                throw new UsageException(FailureKind.Storage, "history_migration_verify_failed");
        }
        await target.ValidateContentAsync(cancellationToken);
        return target;
    }
}
