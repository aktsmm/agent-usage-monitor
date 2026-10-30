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
    public List<AccountProfile> ExtraAccounts { get; init; } = [];
    public Dictionary<string, AccountPreference> AccountPrefs { get; init; } = [];

    /// <summary>Primary connection of each service (the legacy single-account fields) followed by the extra accounts.</summary>
    public IReadOnlyList<AccountProfile> Profiles()
    {
        var list = new List<AccountProfile>();
        if (CopilotEnabled || GithubLogin.Length > 0)
            list.Add(new() { Id = AccountNames.PrimaryId(Provider.Copilot), Provider = Provider.Copilot, Enabled = CopilotEnabled,
                PinnedAccount = GithubPinnedAccount, Login = GithubLogin, CredentialTarget = GithubCredentialTarget, Executable = GithubExecutable });
        if (CodexEnabled || CodexEmail.Length > 0)
            list.Add(new() { Id = AccountNames.PrimaryId(Provider.Codex), Provider = Provider.Codex, Enabled = CodexEnabled,
                PinnedAccount = CodexPinnedAccount, Email = CodexEmail, Home = CodexHome, CliFallback = CodexCliFallback, Executable = CodexExecutable });
        if (ClaudeEnabled || ClaudeAccountLabel.Length > 0)
            list.Add(new() { Id = AccountNames.PrimaryId(Provider.Claude), Provider = Provider.Claude, Enabled = ClaudeEnabled,
                Label = ClaudeAccountLabel, Snapshot = ClaudeSnapshot });
        list.AddRange(ExtraAccounts);
        return list;
    }

    /// <summary>Profiles with their display names and preferences. A primary account is shown in the strip unless the user says otherwise.</summary>
    public IReadOnlyList<AccountEntry> Accounts()
    {
        var prefs = new Dictionary<string, AccountPreference>(AccountPrefs);
        foreach (var provider in Enum.GetValues<Provider>())
            prefs.TryAdd(AccountNames.PrimaryId(provider), new AccountPreference { ShowInStrip = true });
        return AccountNames.Resolve(Profiles(), prefs);
    }

    public DesktopSettings WithPreference(string profileId, AccountPreference preference) =>
        this with { AccountPrefs = new(AccountPrefs) { [profileId] = preference with { Alias = AccountNames.NormalizeAlias(preference.Alias) } } };

    public DesktopSettings AddAccount(AccountProfile profile)
    {
        profile.Validate();
        if (profile.Id.StartsWith("primary-", StringComparison.Ordinal) ||
            Profiles().Any(p => p.Id == profile.Id || p.IsSameAccountAs(profile)))
            throw new UsageException(FailureKind.Schema, "account_profile_duplicate");
        return this with { ExtraAccounts = [.. ExtraAccounts, profile] };
    }

    public DesktopSettings RemoveAccount(string profileId) => this with
    {
        ExtraAccounts = ExtraAccounts.Where(p => p.Id != profileId).ToList(),
        AccountPrefs = AccountPrefs.Where(p => p.Key != profileId).ToDictionary(p => p.Key, p => p.Value)
    };

    /// <summary>Connection identity of one profile; changes whenever what the connection reads changes.</summary>
    public static string SelectionId(AccountProfile p) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new[] { p.Provider.ToString(), p.Enabled.ToString(), p.Identity, p.CredentialTarget ?? "", p.Home,
            p.Snapshot, p.Executable, p.PinnedAccount ?? "", p.VscodeUserId ?? "" })));

    public static IUsageProvider? CreateProvider(AccountProfile p, UsageHttp http) => !p.Enabled ? null : p.Provider switch
    {
        Provider.Copilot => p.VscodeUserId is { } userId
            ? new VscodeCopilotProvider(p.Login, userId, p.PinnedAccount)
            : new CopilotProvider(http, p.Login, p.CredentialTarget, p.PinnedAccount,
                string.IsNullOrWhiteSpace(p.Executable) ? null : p.Executable),
        Provider.Codex => new CodexProvider(http, p.Home, p.Email, p.Executable, p.CliFallback, p.PinnedAccount),
        _ => new ClaudeProvider(p.Snapshot, AccountKey.FromIdentity(Provider.Claude, p.Label))
    };

    public void Validate()
    {
        if (ExtraAccounts is null || AccountPrefs is null) throw new UsageException(FailureKind.Schema, "settings_invalid");
        foreach (var extra in ExtraAccounts) extra.Validate();
        if (ExtraAccounts.Select(p => p.Id).Distinct().Count() != ExtraAccounts.Count ||
            ExtraAccounts.Any(p => p.Id.StartsWith("primary-", StringComparison.Ordinal)))
            throw new UsageException(FailureKind.Schema, "account_profile_duplicate");
        var profiles = Profiles().Where(p => p.Identity.Length > 0).ToArray();
        for (var i = 0; i < profiles.Length; i++)
            for (var j = i + 1; j < profiles.Length; j++)
                if (profiles[i].IsSameAccountAs(profiles[j]))
                    throw new UsageException(FailureKind.Schema, "account_profile_duplicate");
        foreach (var preference in AccountPrefs.Values) AccountNames.NormalizeAlias(preference.Alias);
        ValidatePrimary();
    }

    private void ValidatePrimary()
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
            _ => this with { ClaudeEnabled = true, ClaudeMode = ConnectionMode.Automatic,
                ClaudeAccountLabel = probe.Identity, ClaudeSnapshot = probe.Home }
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
        var extraAccounts = ExtraAccounts.Select(p => p.ExpectedAccount).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in new[] { Provider.Copilot, Provider.Codex })
        {
            // Values of additional accounts are not evidence of who the primary account is.
            var keys = state.Latest.Where(s => s.Key.Account.Provider == provider && !extraAccounts.Contains(s.Key.Account.Id))
                .Select(s => s.Key.Account.Id).Distinct().ToArray();
            if (Mode(provider) == ConnectionMode.Manual && PinnedAccount(provider) is null && keys.Length == 1)
                updated = updated.PinAccount(provider, keys[0]);
        }
        return updated;
    }

    public DesktopSettings CompleteDiscovery(DesktopSettings selection, ConnectionProbe probe, bool initial)
    {
        var updated = this with { ConnectionResults = new(ConnectionResults) { [probe.Provider] = probe } };
        if (probe.Provider == Provider.Claude && probe.Status == DiscoveryStatus.Ready &&
            Mode(Provider.Claude) is not (ConnectionMode.Manual or ConnectionMode.Disabled) &&
            selection.SelectionId(Provider.Claude) == SelectionId(Provider.Claude))
            return updated.ApplyProbe(probe);
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
