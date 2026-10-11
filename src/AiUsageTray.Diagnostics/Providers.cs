using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiUsageTray.Core;

namespace AiUsageTray.Diagnostics;

public abstract class SafeProvider : IUsageProvider
{
    public async Task<FetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        try { return await FetchCoreAsync(cancellationToken); }
        catch (UsageException ex)
        {
            return FetchResult.Failed(new(ex.Kind, ex.SafeCode, DateTimeOffset.UtcNow, ex.RetryAfter));
        }
        catch (JsonException) { return FetchResult.Failed(new(FailureKind.Schema, "invalid_json", DateTimeOffset.UtcNow)); }
        catch (HttpRequestException) { return FetchResult.Failed(new(FailureKind.Network, "network_error", DateTimeOffset.UtcNow)); }
        catch (Win32Exception) { return FetchResult.Failed(new(FailureKind.Cli, "executable_unavailable", DateTimeOffset.UtcNow)); }
        catch (FileNotFoundException) { return FetchResult.Failed(new(FailureKind.MissingCredential, "file_not_found", DateTimeOffset.UtcNow)); }
        catch (DirectoryNotFoundException) { return FetchResult.Failed(new(FailureKind.MissingCredential, "directory_not_found", DateTimeOffset.UtcNow)); }
        catch (IOException) { return FetchResult.Failed(new(FailureKind.Storage, "provider_file_read_failed", DateTimeOffset.UtcNow)); }
        catch (UnauthorizedAccessException) { return FetchResult.Failed(new(FailureKind.Forbidden, "file_access_denied", DateTimeOffset.UtcNow)); }
    }

    protected abstract Task<FetchResult> FetchCoreAsync(CancellationToken cancellationToken);
}

public sealed class CopilotProvider(UsageHttp http, string expectedLogin, string? credentialTarget = null) : SafeProvider
{
    protected override async Task<FetchResult> FetchCoreAsync(CancellationToken cancellationToken)
    {
        var token = credentialTarget is null
            ? (await ReadOnlyProcess.CaptureAsync("gh", ["auth", "token", "--hostname", "github.com"], cancellationToken)).Trim()
            : WindowsCredential.Read(credentialTarget);
        if (string.IsNullOrWhiteSpace(token))
            throw new UsageException(FailureKind.MissingCredential, "github_token_missing");
        using var identity = await http.GetAsync(new("https://api.github.com/user"), "token", token, cancellationToken);
        var login = QuotaParsers.Text(identity.RootElement, "login");
        if (!string.Equals(login, expectedLogin, StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.AccountMismatch, "github_account_mismatch");
        var id = QuotaParsers.Number(identity.RootElement, "id") ??
            throw new UsageException(FailureKind.Schema, "github_user_id_missing");
        var account = AccountKey.FromIdentity(Provider.Copilot, $"github.com:{id}");
        using var usage = await http.GetAsync(new("https://api.github.com/copilot_internal/user"),
            "token", token, cancellationToken);
        return new(QuotaParsers.Copilot(usage.RootElement, account, DateTimeOffset.UtcNow), []);
    }
}

public sealed record CodexCredential([property: JsonIgnore] string AccessToken, string AccountId, string Email)
{
    public static async Task<CodexCredential> ReadAsync(string codexHome, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(codexHome, "auth.json"));
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new UsageException(FailureKind.Schema, "codex_auth_object_expected");
        var mode = QuotaParsers.Text(root, "auth_mode");
        if (mode is not null && mode != "chatgpt" ||
            !string.IsNullOrEmpty(QuotaParsers.Text(root, "OPENAI_API_KEY")))
            throw new UsageException(FailureKind.MissingCredential, "codex_selected_auth_not_subscription");
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            throw new UsageException(FailureKind.MissingCredential, "codex_subscription_auth_missing");
        var access = QuotaParsers.Text(tokens, "access_token");
        var id = QuotaParsers.Text(tokens, "account_id");
        var idToken = QuotaParsers.Text(tokens, "id_token");
        if (access is null || id is null || idToken is null)
            throw new UsageException(FailureKind.MissingCredential, "codex_auth_fields_missing");
        var pieces = idToken.Split('.');
        if (pieces.Length != 3)
            throw new UsageException(FailureKind.Schema, "codex_id_metadata_invalid");
        var encoded = pieces[1].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        JsonDocument metadata;
        try { metadata = JsonDocument.Parse(Convert.FromBase64String(encoded)); }
        catch (FormatException) { throw new UsageException(FailureKind.Schema, "codex_id_metadata_invalid"); }
        using (metadata)
        {
            var email = QuotaParsers.Text(metadata.RootElement, "email");
            if (email is null)
                throw new UsageException(FailureKind.AccountMismatch, "codex_email_metadata_missing");
            return new(access, id, email);
        }
    }

    public override string ToString() => "CodexCredential [redacted]";
}

public sealed class CodexProvider(UsageHttp http, string codexHome, string expectedEmail,
    string codexExecutable, bool allowCliFallback) : SafeProvider
{
    protected override async Task<FetchResult> FetchCoreAsync(CancellationToken cancellationToken)
    {
        var credential = await CodexCredential.ReadAsync(codexHome, cancellationToken);
        if (!string.Equals(credential.Email, expectedEmail, StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.AccountMismatch, "codex_account_mismatch");
        var account = AccountKey.FromIdentity(Provider.Codex, credential.AccountId);
        try
        {
            using var usage = await http.GetAsync(new("https://chatgpt.com/backend-api/wham/usage"),
                "Bearer", credential.AccessToken, cancellationToken, credential.AccountId);
            var returnedId = QuotaParsers.Text(usage.RootElement, "account_id");
            if (returnedId is not null && returnedId != credential.AccountId)
                throw new UsageException(FailureKind.AccountMismatch, "codex_response_account_mismatch");
            return new(QuotaParsers.CodexHttp(usage.RootElement, account, DateTimeOffset.UtcNow), []);
        }
        catch (UsageException ex) when (allowCliFallback &&
            ex.Kind is FailureKind.Unauthorized or FailureKind.Schema)
        {
            var result = await FetchCliAsync(account, cancellationToken);
            return result with { Failures = [new(ex.Kind, "direct_" + ex.SafeCode, DateTimeOffset.UtcNow)] };
        }
        catch (JsonException) when (allowCliFallback)
        {
            var result = await FetchCliAsync(account, cancellationToken);
            return result with { Failures = [new(FailureKind.Schema, "direct_invalid_json", DateTimeOffset.UtcNow)] };
        }
    }

    public async Task<FetchResult> FetchCliAsync(AccountKey account, CancellationToken cancellationToken)
    {
        using var process = ReadOnlyProcess.Start(codexExecutable, ["app-server"], codexHome);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var errors = ReadOnlyProcess.DiscardErrorsAsync(process, deadline.Token);
        try
        {
            await ReadOnlyProcess.RpcAsync(process, 1, "initialize",
                new { clientInfo = new { name = "ai_usage_tray", title = "AI Usage Tray", version = "0.1" } }, deadline.Token);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            var identity = await ReadOnlyProcess.RpcAsync(process, 2, "account/read", new { refreshToken = false }, deadline.Token);
            if (!identity.TryGetProperty("account", out var selected) ||
                QuotaParsers.Text(selected, "type") != "chatgpt" ||
                !string.Equals(QuotaParsers.Text(selected, "email"), expectedEmail, StringComparison.OrdinalIgnoreCase))
                throw new UsageException(FailureKind.AccountMismatch, "codex_cli_account_mismatch");
            var usage = await ReadOnlyProcess.RpcAsync(process, 3, "account/rateLimits/read", null, deadline.Token);
            // Re-read local selection: official refresh may update auth.json, but may not switch the pinned account.
            var after = await CodexCredential.ReadAsync(codexHome, deadline.Token);
            if (AccountKey.FromIdentity(Provider.Codex, after.AccountId) != account)
                throw new UsageException(FailureKind.AccountMismatch, "codex_cli_account_changed");
            return new(QuotaParsers.CodexCli(usage, account, QuotaParsers.Text(selected, "planType"),
                DateTimeOffset.UtcNow), []);
        }
        finally
        {
            await ReadOnlyProcess.CloseAsync(process);
            try { await errors; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }
}

public sealed class ClaudeProvider(string snapshotPath, AccountKey expectedAccount) : SafeProvider
{
    protected override async Task<FetchResult> FetchCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(snapshotPath))
            throw new UsageException(FailureKind.MissingSnapshot, "claude_snapshot_missing");
        await using var stream = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return new(QuotaParsers.ClaudeSnapshot(json.RootElement, expectedAccount), []);
    }
}
