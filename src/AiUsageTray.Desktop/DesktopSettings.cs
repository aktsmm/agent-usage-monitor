using System;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Core;
using AiUsageTray.Diagnostics;

namespace AiUsageTray.Desktop;

public sealed record DesktopSettings
{
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
    public bool StartAtLogon { get; init; }

    public void Validate()
    {
        if (CopilotEnabled && string.IsNullOrWhiteSpace(GithubLogin) ||
            CodexEnabled && (string.IsNullOrWhiteSpace(CodexEmail) ||
                string.IsNullOrWhiteSpace(CodexHome) || (CodexCliFallback && !File.Exists(CodexExecutable))) ||
            ClaudeEnabled && (string.IsNullOrWhiteSpace(ClaudeSnapshot) || string.IsNullOrWhiteSpace(ClaudeAccountLabel)) ||
            Theme is not ("Dark" or "Light"))
            throw new UsageException(FailureKind.Schema, "接続設定のアカウント・パスを確認してください。");
    }

    public IUsageProvider? CreateProvider(Provider provider, UsageHttp http) => provider switch
    {
        Provider.Copilot when CopilotEnabled => new CopilotProvider(http, GithubLogin, GithubCredentialTarget),
        Provider.Codex when CodexEnabled => new CodexProvider(http, CodexHome, CodexEmail, CodexExecutable, CodexCliFallback),
        Provider.Claude when ClaudeEnabled => new ClaudeProvider(ClaudeSnapshot, AccountKey.FromIdentity(Provider.Claude, ClaudeAccountLabel)),
        _ => null
    };

    public string SelectionId(Provider provider)
    {
        string[] fields = provider switch
        {
            Provider.Copilot => [CopilotEnabled.ToString(), GithubLogin, GithubCredentialTarget ?? ""],
            Provider.Codex => [CodexEnabled.ToString(), CodexEmail, CodexHome],
            _ => [ClaudeEnabled.ToString(), ClaudeAccountLabel, ClaudeSnapshot]
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
    }

    public static async Task<DesktopSettings> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new();
        await using var file = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<DesktopSettings>(file, StateStore.JsonOptions, cancellationToken)
            ?? throw new UsageException(FailureKind.Storage, "設定ファイルが空です。");
        settings.Validate();
        return settings;
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
        var executable = Environment.ProcessPath ?? throw new UsageException(FailureKind.Storage, "実行パスが不明です。");
        if (!executable.EndsWith("AiUsageTray.Desktop.exe", StringComparison.OrdinalIgnoreCase))
            throw new UsageException(FailureKind.Storage, "ログオン時起動は公開済みEXEから設定してください。");
        StartupRegistration.Set(enabled, executable);
    }
}
