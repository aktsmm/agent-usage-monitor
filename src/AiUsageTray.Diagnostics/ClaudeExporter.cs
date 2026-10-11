using System.Text.Json;
using AiUsageTray.Core;

namespace AiUsageTray.Diagnostics;

public static class ClaudeExporter
{
    public static async Task ExportAsync(TextReader input, string accountLabel, string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountLabel))
            throw new UsageException(FailureKind.AccountMismatch, "claude_account_label_required");
        var text = await input.ReadToEndAsync(cancellationToken);
        if (text.Length > 1_048_576)
            throw new UsageException(FailureKind.Schema, "claude_statusline_input_too_large");
        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetProperty("rate_limits", out var limits) ||
            limits.ValueKind != JsonValueKind.Object)
            throw new UsageException(FailureKind.MissingSnapshot, "claude_statusline_rate_limits_unavailable");
        var windows = new Dictionary<string, object>();
        foreach (var name in new[] { "five_hour", "seven_day" })
        {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
                continue;
            var used = QuotaParsers.Number(window, "used_percentage");
            var reset = QuotaParsers.Number(window, "resets_at");
            if (used is null || reset is null) continue;
            windows.Add(name, new { used_percentage = used, resets_at = reset });
        }
        if (windows.Count == 0)
            throw new UsageException(FailureKind.MissingSnapshot, "claude_statusline_windows_unavailable");
        var account = AccountKey.FromIdentity(Provider.Claude, accountLabel);
        var payload = JsonSerializer.SerializeToElement(new
        { account_id = account.Id, observed_at = DateTimeOffset.UtcNow, rate_limits = windows });
        QuotaParsers.ClaudeSnapshot(payload, account);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, payload.GetRawText(), cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
