using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageTray.Core;

public sealed class StateStore : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string path;
    private readonly FileStream lease;

    public StateStore(string path)
    {
        this.path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        lease = new FileStream(this.path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public async Task<UsageState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var state = await JsonSerializer.DeserializeAsync<UsageState>(file, JsonOptions, cancellationToken)
            ?? throw new UsageException(FailureKind.Storage, "Empty state file.");
        state.Validate();
        HistoryRetention.Prune(state, DateTimeOffset.UtcNow);
        return state;
    }

    public async Task SaveAsync(UsageState state, CancellationToken cancellationToken)
    {
        state.Validate();
        HistoryRetention.Prune(state, DateTimeOffset.UtcNow);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, state, JsonOptions, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public void Dispose() => lease.Dispose();
}
