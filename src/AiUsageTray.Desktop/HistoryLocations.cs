using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public enum HistoryLocation { Folder, ArchiveFolder, SummaryFile, ArchiveFile }
public sealed record HistoryExplorerTarget(string Path, bool SelectFile);

public static class HistoryLocations
{
    public static async Task<HistoryExplorerTarget> ResolveAsync(HistoryStore store,
        HistoryLocation location, DateOnly? month, CancellationToken cancellationToken)
    {
        await store.ValidateOwnershipAsync(cancellationToken);
        string path = location switch
        {
            HistoryLocation.Folder => store.Root,
            HistoryLocation.ArchiveFolder => store.ArchiveFolder,
            HistoryLocation.SummaryFile when month is { } date => store.SummaryPath(date),
            HistoryLocation.ArchiveFile when month is { } date => store.ArchivePath(date),
            _ => throw new UsageException(FailureKind.Schema, "history_open_location_required")
        };
        bool file = location is HistoryLocation.SummaryFile or HistoryLocation.ArchiveFile;
        if (file)
        {
            if (!File.Exists(path))
                throw new UsageException(FailureKind.Storage, "history_file_missing");
            using var readable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        else
        {
            if (!Directory.Exists(path))
                throw new UsageException(FailureKind.Storage, "history_folder_missing");
            _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
        }
        return new(path, file);
    }

    public static async Task OpenAsync(HistoryStore store, HistoryLocation location, DateOnly? month,
        CancellationToken cancellationToken)
    {
        var target = await ResolveAsync(store, location, month, cancellationToken);
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var info = new ProcessStartInfo(explorer) { UseShellExecute = false };
        if (target.SelectFile) info.ArgumentList.Add("/select,");
        info.ArgumentList.Add(target.Path);
        try
        {
            using var process = Process.Start(info)
                ?? throw new UsageException(FailureKind.Storage, "history_explorer_failed");
        }
        catch (Win32Exception)
        {
            throw new UsageException(FailureKind.Storage, "history_explorer_failed");
        }
    }
}
