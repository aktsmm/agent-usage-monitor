using System.Text.Json;
using AiUsageTray.Core;
using AiUsageTray.Diagnostics;

if (args.Length == 0 || args[0] == "--help")
{
    Console.WriteLine("""
        Usage-only diagnostics (.NET 10).
        copilot <expected-login> [--credential-target <exact-Windows-target>] [--state <file>] [--watch]
        codex <expected-email> --executable <codex.exe> [--home <CODEX_HOME>] [--cli-fallback] [--cli-only] [--state <file>] [--watch]
        claude <snapshot-file> <account-label> [--state <file>] [--watch]
        claude-export <account-label> <snapshot-file>  (official statusline JSON on stdin; no stdout)
        """);
    return 0;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args[0] == "claude-export" && args.Length == 3)
    {
        await ClaudeExporter.ExportAsync(Console.In, args[1], args[2], cancellation.Token);
        return 0;
    }
    if (args.Length < 2)
        throw new UsageException(FailureKind.Schema, "expected_account_required");
    string? Option(string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0)
            return null;
        return index + 1 < args.Length ? args[index + 1]
            : throw new UsageException(FailureKind.Schema, "option_value_required");
    }
    using var http = new UsageHttp();
    IUsageProvider provider = args[0] switch
    {
        "copilot" => new CopilotProvider(http, args[1], Option("--credential-target")),
        "codex" => new CodexProvider(http,
            Option("--home") ?? Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            args[1], Option("--executable") ?? throw new UsageException(FailureKind.Cli, "codex_executable_required"),
            args.Contains("--cli-fallback")),
        "claude" when args.Length >= 3 => new ClaudeProvider(args[1], AccountKey.FromIdentity(Provider.Claude, args[2])),
        _ => throw new UsageException(FailureKind.Schema, "provider_not_supported")
    };
    if (args.Contains("--cli-only") && provider is CodexProvider codex)
    {
        var home = Option("--home") ?? Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var credential = await CodexCredential.ReadAsync(home, cancellation.Token);
        if (!string.Equals(credential.Email, args[1], StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.AccountMismatch, "codex_account_mismatch");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var result = await codex.FetchCliAsync(AccountKey.FromIdentity(Provider.Codex, credential.AccountId), deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, StateStore.JsonOptions));
        return result.Succeeded ? 0 : 1;
    }
    var statePath = Option("--state") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiUsageTray", $"diagnostics-{args[0]}.json");
    using var store = new StateStore(statePath);
    var state = await store.LoadAsync(cancellation.Token);
    bool succeeded = false;
    await Polling.RunAsync(provider, state, store, (result, notifications) =>
    {
        succeeded = result.Succeeded;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.Succeeded, result.Snapshots, result.Failures, Notifications = notifications,
            LastConfirmed = state.Latest.Select(s => new
            {
                Snapshot = s, Stale = !s.IsFresh(DateTimeOffset.UtcNow, state.Polling.MaximumAge),
                Daily = Budgets.Summarize(state, s, false), Weekly = Budgets.Summarize(state, s, true)
            })
        }, StateStore.JsonOptions));
        return Task.CompletedTask;
    }, once: !args.Contains("--watch"), cancellation.Token);
    return succeeded ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled_or_timeout");
    return 2;
}
catch (UsageException ex)
{
    Console.Error.WriteLine($"{ex.Kind}:{ex.SafeCode}");
    return 1;
}
catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine($"diagnostics_error:{ex.GetType().Name}");
    return 1;
}
