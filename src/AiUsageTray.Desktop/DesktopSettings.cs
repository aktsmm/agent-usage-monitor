using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Core;
using AiUsageTray.Diagnostics;

namespace AiUsageTray.Desktop;

public enum ConnectionMode { Pending, Automatic, Manual, Disabled, Attempted, NeedsConfirmation }

public sealed record DesktopSettings
{
    public int Version { get; init; } = 1;
    public ConnectionMode CopilotMode { get; init; }
    public ConnectionMode CodexMode { get; init; }
    public ConnectionMode ClaudeMode { get; init; }
    public string? GithubPinnedAccount { get; init; }
    public string GithubExecutable { get; init; } = "";
    public string? CodexPinnedAccount { get; init; }
    public bool AutoUpdateEnabled { get; init; } = true;
    public Dictionary<Provider, ConnectionProbe> ConnectionResults { get; init; } = [];
    public bool CopilotEnabled { get; init; }
    public string GithubLogin { get; init; } = "";
    public string? GithubCredentialTarget { get; init; }
    public bool CodexEnabled { get; init; }
    public string CodexEmail { get; init; } = "";
    public string CodexHome { get; init; } = Environment.GetEnvironmentVariable("CODEX_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public string CodexExecutable { get; init; } = "";
    public bool CodexCliFallback { get; init; }
    public bool ClaudeEnabled { get; init; }
    public string ClaudeSnapshot { get; init; } = "";
    public string ClaudeAccountLabel { get; init; } = "";
    public string Theme { get; init; } = "Dark";
    public DisplayLanguage Language { get; init; } = DisplayLanguage.System;
    public bool UsageStripEnabled { get; init; } = true;
    public UsageStripPosition? UsageStripPosition { get; init; }
    public bool StartAtLogon { get; init; }

    public void Validate()
    {
        if (Version != 1 || ConnectionResults is null || !Enum.IsDefined(Language) ||
            !Enum.IsDefined(CopilotMode) || !Enum.IsDefined(CodexMode) || !Enum.IsDefined(ClaudeMode) ||
            CopilotEnabled && string.IsNullOrWhiteSpace(GithubLogin) ||
            CodexEnabled && (string.IsNullOrWhiteSpace(CodexEmail) ||
                string.IsNullOrWhiteSpace(CodexHome) || (CodexCliFallback && !File.Exists(CodexExecutable))) ||
            ClaudeEnabled && (string.IsNullOrWhiteSpace(ClaudeSnapshot) || string.IsNullOrWhiteSpace(ClaudeAccountLabel)) ||
            Theme is not ("Dark" or "Light"))
            throw new UsageException(FailureKind.Schema, "settings_invalid");
        if (GithubPinnedAccount is { } github && (github.Length != 64 || !IsHex(github)) ||
            CodexPinnedAccount is { } codex && (codex.Length != 64 || !IsHex(codex)))
            throw new UsageException(FailureKind.Schema, "settings_pin_invalid");
        if (CopilotMode == ConnectionMode.Automatic && (!CopilotEnabled || GithubPinnedAccount is null) ||
            CodexMode == ConnectionMode.Automatic && (!CodexEnabled || CodexPinnedAccount is null))
            throw new UsageException(FailureKind.Schema, "settings_auto_pin_required");
    }

    private static bool IsHex(string value) => System.Linq.Enumerable.All(value, Uri.IsHexDigit);

    public IUsageProvider? CreateProvider(Provider provider, UsageHttp http) => provider switch
    {
        Provider.Copilot when CopilotEnabled => new CopilotProvider(http, GithubLogin, GithubCredentialTarget,
            GithubPinnedAccount, string.IsNullOrWhiteSpace(GithubExecutable) ? null : GithubExecutable),
        Provider.Codex when CodexEnabled => new CodexProvider(http, CodexHome, CodexEmail, CodexExecutable, CodexCliFallback, CodexPinnedAccount),
        Provider.Claude when ClaudeEnabled => new ClaudeProvider(ClaudeSnapshot, AccountKey.FromIdentity(Provider.Claude, ClaudeAccountLabel)),
        _ => null
    };

    public string SelectionId(Provider provider)
    {
        string[] fields = provider switch
        {
            Provider.Copilot => [CopilotEnabled.ToString(), GithubLogin, GithubCredentialTarget ?? "", GithubPinnedAccount ?? ""],
            Provider.Codex => [CodexEnabled.ToString(), CodexEmail, CodexHome, CodexPinnedAccount ?? ""],
            _ => [ClaudeEnabled.ToString(), ClaudeAccountLabel, ClaudeSnapshot]
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
    }

    public static async Task<DesktopSettings> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new();
        await using var file = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken);
        var settings = document.RootElement.Deserialize<DesktopSettings>(StateStore.JsonOptions)
            ?? throw new UsageException(FailureKind.Storage, "settings_file_empty");
        if (!document.RootElement.TryGetProperty("Version", out _) && !document.RootElement.TryGetProperty("version", out _))
            settings = MigrateLegacy(settings);
        settings.Validate();
        return settings;
    }

    public static DesktopSettings MigrateLegacy(DesktopSettings value) => value with
    {
        Version = 1,
        CopilotMode = value.CopilotEnabled || value.GithubLogin.Length > 0 || value.GithubCredentialTarget is not null
            ? ConnectionMode.Manual : ConnectionMode.NeedsConfirmation,
        CodexMode = value.CodexEnabled || value.CodexEmail.Length > 0 || value.CodexExecutable.Length > 0 ||
            value.CodexCliFallback || value.CodexHome != new DesktopSettings().CodexHome
            ? ConnectionMode.Manual : ConnectionMode.NeedsConfirmation,
        ClaudeMode = value.ClaudeEnabled || value.ClaudeAccountLabel.Length > 0 || value.ClaudeSnapshot.Length > 0
            ? ConnectionMode.Manual : ConnectionMode.NeedsConfirmation
    };

    public ConnectionMode Mode(Provider provider) => provider switch
    {
        Provider.Copilot => CopilotMode, Provider.Codex => CodexMode, _ => ClaudeMode
    };

    public DesktopSettings WithMode(Provider provider, ConnectionMode mode) => provider switch
    {
        Provider.Copilot => this with { CopilotMode = mode },
        Provider.Codex => this with { CodexMode = mode },
        _ => this with { ClaudeMode = mode }
    };

    public DesktopSettings ApplyProbe(ConnectionProbe probe)
    {
        if (probe.Status != DiscoveryStatus.Ready || probe.AccountKey.Length != 64)
            throw new UsageException(FailureKind.Schema, "settings_candidate_missing");
        return probe.Provider switch
        {
            Provider.Copilot => this with { CopilotEnabled = true, CopilotMode = ConnectionMode.Automatic,
                GithubLogin = probe.Identity, GithubPinnedAccount = probe.AccountKey,
                GithubExecutable = probe.Executable, GithubCredentialTarget = null },
            Provider.Codex => this with { CodexEnabled = true, CodexMode = ConnectionMode.Automatic,
                CodexEmail = probe.Identity, CodexPinnedAccount = probe.AccountKey, CodexHome = probe.Home,
                CodexExecutable = probe.Executable, CodexCliFallback = probe.Executable.Length > 0 },
            _ => throw new UsageException(FailureKind.Forbidden, "settings_claude_confirmation_required")
        };
    }
    public DesktopSettings PinAccount(Provider provider, string key) => provider switch
    {
        Provider.Copilot => this with { GithubPinnedAccount = key },
        Provider.Codex => this with { CodexPinnedAccount = key },
        _ => this
    };

    public string? PinnedAccount(Provider provider) => provider switch
    {
        Provider.Copilot => GithubPinnedAccount, Provider.Codex => CodexPinnedAccount, _ => null
    };

    public DesktopSettings PinExisting(UsageState state)
    {
        var updated = this;
        foreach (var provider in new[] { Provider.Copilot, Provider.Codex })
        {
            var keys = state.Latest.Where(s => s.Key.Account.Provider == provider)
                .Select(s => s.Key.Account.Id).Distinct().ToArray();
            if (Mode(provider) == ConnectionMode.Manual && PinnedAccount(provider) is null && keys.Length == 1)
                updated = updated.PinAccount(provider, keys[0]);
        }
        return updated;
    }

    public DesktopSettings CompleteDiscovery(DesktopSettings selection, ConnectionProbe probe, bool initial)
    {
        var updated = this with { ConnectionResults = new(ConnectionResults) { [probe.Provider] = probe } };
        return initial && selection.Mode(probe.Provider) == ConnectionMode.Attempted &&
            Mode(probe.Provider) == ConnectionMode.Attempted &&
            selection.SelectionId(probe.Provider) == SelectionId(probe.Provider) &&
            probe.Status == DiscoveryStatus.Ready ? updated.ApplyProbe(probe) : updated;
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        Validate();
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, StateStore.JsonOptions), cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void SetStartAtLogon(bool enabled)
    {
        var executable = Environment.ProcessPath ?? throw new UsageException(FailureKind.Storage, "settings_executable_missing");
        if (!executable.EndsWith("AiUsageTray.Desktop.exe", StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.Storage, "settings_startup_executable_required");
        StartupRegistration.Set(enabled, executable);
    }
}
