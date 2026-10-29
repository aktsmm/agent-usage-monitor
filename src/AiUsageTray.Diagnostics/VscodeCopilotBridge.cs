using System.Globalization;
using System.Text.Json;
using AiUsageTray.Core;

namespace AiUsageTray.Diagnostics;

public sealed record VscodeAccountCandidate(string Login, string UserId, string Label, DateTimeOffset ObservedAt);

public static class VscodeCopilotBridge
{
    public const int MaximumSnapshotBytes = 1_048_576;
    private const int MaximumManifestBytes = 65_536;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiUsageTray", "vscode-bridge");

    public static string SnapshotPath(string userId, string? directory = null)
    {
        if (!ulong.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            throw new UsageException(FailureKind.Schema, "vscode_account_id_invalid");
        var root = directory ?? DefaultDirectory;
        if (!Path.IsPathFullyQualified(root))
            throw new UsageException(FailureKind.Schema, "vscode_bridge_path_invalid");
        return Path.Combine(root, id.ToString(CultureInfo.InvariantCulture) + ".json");
    }

    public static async Task<IReadOnlyList<VscodeAccountCandidate>> DiscoverAccountsAsync(
        string? directory = null, CancellationToken cancellationToken = default)
    {
        var root = directory ?? DefaultDirectory;
        if (!Path.IsPathFullyQualified(root))
            throw new UsageException(FailureKind.Schema, "vscode_bridge_path_invalid");
        var path = Path.Combine(root, "accounts.json");
        if (!File.Exists(path)) return [];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = await ParseLimitedAsync(stream, MaximumManifestBytes,
            "vscode_manifest_too_large", cancellationToken);
        var manifest = document.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var manifestVersion) || manifestVersion != 1 ||
            !manifest.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array ||
            accounts.GetArrayLength() > 20)
            throw new UsageException(FailureKind.Schema, "vscode_manifest_invalid");

        var candidates = new List<VscodeAccountCandidate>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in accounts.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new UsageException(FailureKind.Schema, "vscode_manifest_invalid");
            var userId = QuotaParsers.Text(item, "userId");
            var login = QuotaParsers.Text(item, "login");
            var label = QuotaParsers.Text(item, "label");
            var observedText = QuotaParsers.Text(item, "observedAt");
            if (userId is null || login is null || label is null ||
                !DateTimeOffset.TryParse(observedText, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var observedAt) ||
                !ulong.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0 ||
                id.ToString(CultureInfo.InvariantCulture) != userId ||
                string.IsNullOrWhiteSpace(login) || login.Length > 39 ||
                string.IsNullOrWhiteSpace(label) || label.Length > 128 ||
                login.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) ||
                label.Any(char.IsControl) || !ids.Add(userId))
                throw new UsageException(FailureKind.Schema, "vscode_manifest_invalid");
            var snapshotPath = SnapshotPath(userId, root);
            if (File.Exists(snapshotPath) && observedAt <= DateTimeOffset.UtcNow.AddMinutes(1) &&
                DateTimeOffset.UtcNow - observedAt <= TimeSpan.FromMinutes(15))
            {
                var result = await new VscodeCopilotProvider(login, userId, snapshotDirectory: root)
                    .FetchAsync(cancellationToken);
                if (result.Succeeded && result.Snapshots.All(snapshot => snapshot.ObservedAt == observedAt))
                    candidates.Add(new(login, userId, label, observedAt));
            }
        }
        return candidates.OrderBy(candidate => candidate.Login, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static async Task<JsonDocument> ParseLimitedAsync(Stream stream, int maximumBytes,
        string errorCode, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + read > maximumBytes)
                throw new UsageException(FailureKind.Schema, errorCode);
            memory.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(memory.ToArray());
    }
}
