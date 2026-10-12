using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiUsageTray.Core;

namespace AiUsageTray.Diagnostics;

public enum DiscoveryStatus { Ready, LoginRequired, Unsupported, Failed, Timeout, Cancelled }

public sealed record ConnectionProbe(Provider Provider, DiscoveryStatus Status, string Code,
    string Identity = "", string AccountKey = "", string Home = "", string Executable = "",
    string Version = "", UsageFailure? Failure = null)
{
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public FetchResult? Usage { get; init; }
}

public sealed class ConnectionDiscovery(UsageHttp http, Func<string, string?>? cliResolver = null,
    Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>>? commands = null)
{
    private readonly Func<string, string?> resolve = cliResolver ?? (name => OfficialCli.Resolve(name));
    private readonly Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>> capture =
        commands ?? ReadOnlyProcess.CaptureResultAsync;
    public async Task<ConnectionProbe> ProbeAsync(Provider provider, string codexHome,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            return provider switch
            {
                Provider.Copilot => await CopilotAsync(deadline.Token),
                Provider.Codex => await CodexAsync(codexHome, deadline.Token),
                _ => await ClaudeAsync(deadline.Token)
            };
        }
        catch (OperationCanceledException)
        {
            return new(provider, cancellationToken.IsCancellationRequested ? DiscoveryStatus.Cancelled : DiscoveryStatus.Timeout,
                cancellationToken.IsCancellationRequested ? "discovery_cancelled" : "discovery_timeout");
        }
        catch (UsageException ex)
        {
            return Failed(provider, new(ex.Kind, ex.SafeCode, DateTimeOffset.UtcNow, ex.RetryAfter));
        }
        catch (JsonException) { return Failed(provider, new(FailureKind.Schema, "discovery_invalid_json", DateTimeOffset.UtcNow)); }
        catch (HttpRequestException) { return Failed(provider, new(FailureKind.Network, "discovery_network", DateTimeOffset.UtcNow)); }
        catch (Win32Exception) { return Failed(provider, new(FailureKind.Cli, "cli_start_failed", DateTimeOffset.UtcNow)); }
        catch (FileNotFoundException) { return Failed(provider, new(FailureKind.MissingCredential, "auth_file_missing", DateTimeOffset.UtcNow)); }
        catch (DirectoryNotFoundException) { return Failed(provider, new(FailureKind.MissingCredential, "auth_directory_missing", DateTimeOffset.UtcNow)); }
        catch (UnauthorizedAccessException) { return Failed(provider, new(FailureKind.Forbidden, "auth_access_denied", DateTimeOffset.UtcNow)); }
        catch (IOException) { return Failed(provider, new(FailureKind.Storage, "auth_read_failed", DateTimeOffset.UtcNow)); }
    }

    public static ConnectionProbe Failed(Provider provider, UsageFailure failure) =>
        new(provider, failure.Kind == FailureKind.MissingCredential ? DiscoveryStatus.LoginRequired : DiscoveryStatus.Failed,
            failure.Code, Failure: failure);

    private async Task<ConnectionProbe> CopilotAsync(CancellationToken cancellationToken)
    {
        var executable = resolve("gh");
        if (executable is null)
            return new(Provider.Copilot, DiscoveryStatus.Unsupported, "github_cli_missing_copilot_selection_unverified");
        var token = await OfficialCli.GithubTokenAsync(executable, null, cancellationToken, capture);
        using var identity = await http.GetAsync(new("https://api.github.com/user"), "token", token, cancellationToken);
        var login = QuotaParsers.Text(identity.RootElement, "login") ??
            throw new UsageException(FailureKind.Schema, "github_user_login_missing");
        var id = QuotaParsers.Number(identity.RootElement, "id") ??
            throw new UsageException(FailureKind.Schema, "github_user_id_missing");
        if (id <= 0 || id != decimal.Truncate(id))
            throw new UsageException(FailureKind.Schema, "github_user_id_invalid");
        var key = Core.AccountKey.FromIdentity(Provider.Copilot, $"github.com:{id.ToString(CultureInfo.InvariantCulture)}");
        using var usage = await http.GetAsync(new("https://api.github.com/copilot_internal/user"),
            "token", token, cancellationToken);
        var result = new FetchResult(QuotaParsers.Copilot(usage.RootElement, key, DateTimeOffset.UtcNow), []);
        RequireQuota(result);
        var afterResult = await capture(executable,
            ["auth", "status", "--active", "--hostname", "github.com", "--json", "hosts"], cancellationToken);
        if (afterResult.ExitCode != 0) throw new UsageException(FailureKind.Cli, "github_status_failed");
        using var after = JsonDocument.Parse(afterResult.Output);
        if (!string.Equals(OfficialCli.ActiveGithubLogin(after.RootElement), login, StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.AccountMismatch, "github_account_changed_during_discovery");
        return new(Provider.Copilot, DiscoveryStatus.Ready, "github_verified", login, key.Id, Executable: executable)
            { Usage = result };
    }

    private async Task<ConnectionProbe> CodexAsync(string home, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(home))
            throw new UsageException(FailureKind.Schema, "absolute_codex_home_required");
        home = Path.GetFullPath(home);
        var executable = resolve("codex") ?? "";
        if (!File.Exists(Path.Combine(home, "auth.json")))
        {
            if (executable.Length > 0)
            {
                var status = await capture(executable, ["login", "status"], cancellationToken);
                if (status.ExitCode == 1)
                    return new(Provider.Codex, DiscoveryStatus.LoginRequired, "codex_login_required",
                        Home: home, Executable: executable);
                if (status.ExitCode != 0)
                    throw new UsageException(FailureKind.Cli, "codex_login_status_failed");
            }
            return new(Provider.Codex, DiscoveryStatus.Unsupported, "codex_file_auth_missing_keyring_unverified",
                Home: home, Executable: executable);
        }
        var credential = await CodexCredential.ReadAsync(home, cancellationToken);
        var key = Core.AccountKey.FromIdentity(Provider.Codex, credential.AccountId);
        var fetcher = new CodexProvider(http, home, credential.Email, executable,
            executable.Length > 0, key.Id);
        var result = await fetcher.FetchAsync(cancellationToken);
        if (!result.Succeeded)
            return Failed(Provider.Codex, result.Failures.Last()) with { Home = home, Executable = executable };
        RequireQuota(result);
        return new(Provider.Codex, DiscoveryStatus.Ready, "codex_verified", credential.Email, key.Id,
            home, executable) { Usage = result };
    }

    private async Task<ConnectionProbe> ClaudeAsync(CancellationToken cancellationToken)
    {
        var executable = resolve("claude");
        if (executable is null)
            return new(Provider.Claude, DiscoveryStatus.Unsupported, "claude_cli_missing");
        var versionResult = await capture(executable, ["--version"], cancellationToken);
        if (versionResult.ExitCode != 0) throw new UsageException(FailureKind.Cli, "claude_version_failed");
        var version = versionResult.Output.Trim();
        if (version.Length > 120)
            throw new UsageException(FailureKind.Schema, "claude_version_schema");
        var (exitCode, output) = await capture(executable, ["auth", "status"], cancellationToken);
        using var status = JsonDocument.Parse(output);
        if (exitCode is not (0 or 1) ||
            !status.RootElement.TryGetProperty("loggedIn", out var loggedIn) ||
            loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new UsageException(FailureKind.Schema, "claude_auth_status_schema");
        if ((exitCode == 0) != loggedIn.GetBoolean())
            throw new UsageException(FailureKind.Schema, "claude_auth_status_exit_mismatch");
        return new(Provider.Claude, loggedIn.GetBoolean() ? DiscoveryStatus.Unsupported : DiscoveryStatus.LoginRequired,
            loggedIn.GetBoolean() ? "claude_scalar_link_required" : "claude_login_required",
            Executable: executable, Version: version);
    }

    private static void RequireQuota(FetchResult result)
    {
        if (!result.Snapshots.Any(s => s.HasMeasurement || s.Unlimited))
            throw new UsageException(FailureKind.Schema, "verified_quota_unavailable");
    }
}
