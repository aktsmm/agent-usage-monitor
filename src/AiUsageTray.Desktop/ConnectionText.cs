using AiUsageTray.Core;
using AiUsageTray.Diagnostics;

namespace AiUsageTray.Desktop;

public static class ConnectionText
{
    public static string Name(Provider provider) => provider switch
    { Provider.Copilot => "GitHub Copilot", Provider.Codex => "Codex", _ => "Claude Code" };

    public static string Failure(UsageFailure failure, UiLocalizer? localizer = null) =>
        (localizer ?? new()).Text(failure.Kind switch
        {
            FailureKind.AccountMismatch => UiText.AccountMismatch,
            FailureKind.Unauthorized => UiText.FailureUnauthorized,
            FailureKind.Forbidden => UiText.FailureForbidden,
            FailureKind.Network => UiText.FailureNetwork,
            FailureKind.Timeout => UiText.FailureTimeout,
            FailureKind.Schema => UiText.FailureSchema,
            FailureKind.RateLimited => UiText.FailureRateLimited,
            FailureKind.Server => UiText.FailureServer,
            FailureKind.MissingCredential => UiText.MissingCredential,
            FailureKind.MissingSnapshot => UiText.FailureSnapshot,
            FailureKind.Cli => UiText.FailureCli,
            _ => UiText.FailureStorage
        });

    public static string Probe(ConnectionProbe probe, UiLocalizer? localizer = null)
    {
        var locale = localizer ?? new();
        UiText? message = probe.Code switch
        {
            "claude_cli_missing" => UiText.ClaudeCliMissing,
            "claude_login_required" => UiText.ClaudeLoginRequired,
            "claude_scalar_link_required" => UiText.ClaudeScalarRequired,
            "github_cli_missing_copilot_selection_unverified" => UiText.GithubCliMissing,
            "codex_file_auth_missing_keyring_unverified" => UiText.CodexFileMissing,
            "codex_login_required" => UiText.CodexLoginRequired,
            "codex_selected_auth_not_subscription" => UiText.CodexNotSubscription,
            "codex_credential_store_unsupported" => UiText.CodexStoreUnsupported,
            _ => null
        };
        if (message is { } key) return locale.Text(key);
        if (probe.Failure is { } failure) return Failure(failure, locale);
        return locale.Text(probe.Status switch
        {
            DiscoveryStatus.Timeout => UiText.DiscoveryTimeout,
            DiscoveryStatus.Cancelled => UiText.DiscoveryCancelled,
            DiscoveryStatus.Ready => UiText.DiscoveryReady,
            _ => UiText.DiscoveryUnknown
        });
    }

    public static string Summary(DesktopSettings settings, Provider provider, UsageFailure? failure,
        bool discovering, UiLocalizer? localizer = null)
    {
        var locale = localizer ?? new(settings.Language);
        if (failure is not null) return Failure(failure, locale);
        var mode = settings.Mode(provider);
        if (mode == ConnectionMode.Disabled) return locale.Text(UiText.ConnectionDisabled);
        if (mode == ConnectionMode.NeedsConfirmation) return locale.Text(UiText.ConnectionConfirm);
        if (discovering && mode is ConnectionMode.Pending or ConnectionMode.Attempted)
            return locale.Text(UiText.ConnectionDiscovering);
        if (mode is ConnectionMode.Manual or ConnectionMode.Automatic)
            return locale.Text(mode == ConnectionMode.Automatic ? UiText.ConnectionAutomatic : UiText.ConnectionManual);
        return settings.ConnectionResults.TryGetValue(provider, out var probe)
            ? Probe(probe, locale) : locale.Text(UiText.ConnectionPending);
    }
}
