using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public static class ConnectionEdits
{
    public static DesktopSettings CopyProvider(DesktopSettings source, DesktopSettings target, Provider provider) => provider switch
    {
        Provider.Copilot => target with { CopilotEnabled = source.CopilotEnabled, CopilotMode = source.CopilotMode,
            GithubLogin = source.GithubLogin, GithubCredentialTarget = source.GithubCredentialTarget,
            GithubPinnedAccount = source.GithubPinnedAccount, GithubExecutable = source.GithubExecutable },
        Provider.Codex => target with { CodexEnabled = source.CodexEnabled, CodexMode = source.CodexMode,
            CodexEmail = source.CodexEmail, CodexHome = source.CodexHome, CodexExecutable = source.CodexExecutable,
            CodexCliFallback = source.CodexCliFallback, CodexPinnedAccount = source.CodexPinnedAccount },
        _ => target with { ClaudeEnabled = source.ClaudeEnabled, ClaudeMode = source.ClaudeMode,
            ClaudeAccountLabel = source.ClaudeAccountLabel, ClaudeSnapshot = source.ClaudeSnapshot }
    };

    public static DesktopSettings Apply(DesktopSettings before, DesktopSettings after)
    {
        if (before.GithubLogin != after.GithubLogin || before.GithubCredentialTarget != after.GithubCredentialTarget)
            after = after with { GithubPinnedAccount = null, CopilotMode = ConnectionMode.Manual };
        if (before.CodexEmail != after.CodexEmail || before.CodexHome != after.CodexHome)
            after = after with { CodexPinnedAccount = null, CodexMode = ConnectionMode.Manual };
        if (before.ClaudeAccountLabel != after.ClaudeAccountLabel || before.ClaudeSnapshot != after.ClaudeSnapshot)
            after = after with { ClaudeMode = ConnectionMode.Manual };
        foreach (var provider in System.Enum.GetValues<Provider>())
        {
            bool enabled = provider switch
            { Provider.Copilot => after.CopilotEnabled, Provider.Codex => after.CodexEnabled, _ => after.ClaudeEnabled };
            bool previous = provider switch
            { Provider.Copilot => before.CopilotEnabled, Provider.Codex => before.CodexEnabled, _ => before.ClaudeEnabled };
            if (!enabled && previous) after = after.WithMode(provider, ConnectionMode.Disabled);
            if (enabled && !previous && after.Mode(provider) != ConnectionMode.Automatic)
                after = after.WithMode(provider, ConnectionMode.Manual);
        }
        return after;
    }
}
