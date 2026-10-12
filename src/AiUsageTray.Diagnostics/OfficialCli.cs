using System.Text.Json;
using AiUsageTray.Core;

namespace AiUsageTray.Diagnostics;

public static class OfficialCli
{
    public static string? Resolve(string name, string? searchPath = null)
    {
        if (name is not ("gh" or "codex" or "claude"))
            throw new UsageException(FailureKind.Forbidden, "cli_name_not_allowed");
        foreach (var entry in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, name + ".exe");
            if (File.Exists(candidate)) return ValidateExecutable(candidate);
            if (name != "codex" || !File.Exists(Path.Combine(directory, "codex.cmd"))) continue;
            // Resolve the official npm package's native binary, never execute a shell shim.
            candidate = Path.Combine(directory, "node_modules", "@openai", "codex", "node_modules",
                "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
            if (File.Exists(candidate)) return ValidateExecutable(candidate);
        }
        return null;
    }

    public static string ValidateExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new UsageException(FailureKind.Cli, "absolute_cli_executable_required");
        return Path.GetFullPath(path);
    }

    public static string ActiveGithubLogin(JsonElement root)
    {
        if (!root.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Object)
            throw new UsageException(FailureKind.Schema, "github_status_schema");
        if (!hosts.TryGetProperty("github.com", out var entries))
            throw new UsageException(FailureKind.MissingCredential, "github_active_login_missing");
        if (entries.ValueKind != JsonValueKind.Array)
            throw new UsageException(FailureKind.Schema, "github_status_schema");
        var active = entries.EnumerateArray().Where(item =>
            item.TryGetProperty("active", out var selected) && selected.ValueKind == JsonValueKind.True).ToArray();
        if (active.Length != 1)
            throw new UsageException(FailureKind.AccountMismatch, "github_active_login_ambiguous");
        if (QuotaParsers.Text(active[0], "state") != "success")
            throw new UsageException(FailureKind.Unauthorized, "github_active_login_invalid");
        return QuotaParsers.Text(active[0], "login")
            ?? throw new UsageException(FailureKind.Schema, "github_active_login_schema");
    }

    public static async Task<string> GithubTokenAsync(string executable, string? expectedLogin,
        CancellationToken cancellationToken,
        Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>>? capture = null)
    {
        executable = ValidateExecutable(executable);
        capture ??= ReadOnlyProcess.CaptureResultAsync;
        var statusResult = await capture(executable,
            ["auth", "status", "--active", "--hostname", "github.com", "--json", "hosts"], cancellationToken);
        if (statusResult.ExitCode != 0) throw new UsageException(FailureKind.Cli, "github_status_failed");
        using var status = JsonDocument.Parse(statusResult.Output);
        var active = ActiveGithubLogin(status.RootElement);
        if (expectedLogin is not null && !string.Equals(active, expectedLogin, StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.AccountMismatch, "github_account_mismatch");
        var tokenResult = await capture(executable, ["auth", "token", "--hostname", "github.com"], cancellationToken);
        if (tokenResult.ExitCode != 0) throw new UsageException(FailureKind.MissingCredential, "github_token_missing");
        var token = tokenResult.Output.Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new UsageException(FailureKind.MissingCredential, "github_token_missing");
        return token;
    }
}
