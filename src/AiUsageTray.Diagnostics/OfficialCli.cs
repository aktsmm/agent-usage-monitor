using System.Text.Json;
using AiUsageTray.Core;

namespace AiUsageTray.Diagnostics;

public sealed record GithubLoginCandidate(string Login, bool Active);

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

    public static IReadOnlyList<GithubLoginCandidate> GithubLoginCandidates(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Object ||
            !hosts.TryGetProperty("github.com", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new UsageException(FailureKind.Schema, "github_status_schema");

        var logins = new List<(string Login, string State, bool Active)>();
        foreach (var item in entries.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new UsageException(FailureKind.Schema, "github_status_schema");
            var login = QuotaParsers.Text(item, "login");
            var state = QuotaParsers.Text(item, "state");
            if (string.IsNullOrWhiteSpace(login) || state is null)
                throw new UsageException(FailureKind.Schema, "github_login_schema");
            var hasActive = item.TryGetProperty("active", out var selected);
            if (hasActive && selected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new UsageException(FailureKind.Schema, "github_login_schema");
            var active = hasActive && selected.ValueKind == JsonValueKind.True;
            logins.Add((login, state, active));
        }

        return logins.GroupBy(item => item.Login, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.All(item => item.State == "success"))
            .Select(group => new GithubLoginCandidate(group.First().Login, group.Any(item => item.Active)))
            .OrderByDescending(candidate => candidate.Active)
            .ThenBy(candidate => candidate.Login, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<IReadOnlyList<GithubLoginCandidate>> DiscoverGithubAccountsAsync(string executable,
        CancellationToken cancellationToken,
        Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>>? capture = null)
    {
        executable = ValidateExecutable(executable);
        capture ??= ReadOnlyProcess.CaptureResultAsync;
        var result = await capture(executable,
            ["auth", "status", "--hostname", "github.com", "--json", "hosts"], cancellationToken);
        if (result.ExitCode != 0)
            throw new UsageException(FailureKind.Cli, "github_status_failed");
        using var status = JsonDocument.Parse(result.Output);
        return GithubLoginCandidates(status.RootElement);
    }

    public static string ConfiguredGithubLogin(JsonElement root, string expectedLogin)
    {
        if (!root.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Object ||
            !hosts.TryGetProperty("github.com", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new UsageException(FailureKind.Schema, "github_status_schema");
        var matches = entries.EnumerateArray().Where(item =>
            string.Equals(QuotaParsers.Text(item, "login"), expectedLogin, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            throw new UsageException(FailureKind.MissingCredential, "github_login_not_configured");
        if (matches.Any(item => QuotaParsers.Text(item, "state") != "success"))
            throw new UsageException(FailureKind.Unauthorized, "github_login_invalid");
        return QuotaParsers.Text(matches[0], "login")
            ?? throw new UsageException(FailureKind.Schema, "github_login_schema");
    }

    public static async Task<string> GithubTokenAsync(string executable, string? expectedLogin,
        CancellationToken cancellationToken,
        Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>>? capture = null)
    {
        executable = ValidateExecutable(executable);
        capture ??= ReadOnlyProcess.CaptureResultAsync;
        var statusArgs = expectedLogin is null
            ? new[] { "auth", "status", "--active", "--hostname", "github.com", "--json", "hosts" }
            : ["auth", "status", "--hostname", "github.com", "--json", "hosts"];
        var statusResult = await capture(executable, statusArgs, cancellationToken);
        if (statusResult.ExitCode != 0) throw new UsageException(FailureKind.Cli, "github_status_failed");
        using var status = JsonDocument.Parse(statusResult.Output);
        var login = expectedLogin is null ? ActiveGithubLogin(status.RootElement)
            : ConfiguredGithubLogin(status.RootElement, expectedLogin);
        var tokenArgs = expectedLogin is null
            ? new[] { "auth", "token", "--hostname", "github.com" }
            : ["auth", "token", "--hostname", "github.com", "--user", login];
        var tokenResult = await capture(executable, tokenArgs, cancellationToken);
        if (tokenResult.ExitCode != 0) throw new UsageException(FailureKind.MissingCredential, "github_token_missing");
        var token = tokenResult.Output.Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new UsageException(FailureKind.MissingCredential, "github_token_missing");
        return token;
    }
}
