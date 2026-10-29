using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AiUsageTray.Core;
using AiUsageTray.Diagnostics;

namespace AiUsageTray.Desktop;

internal sealed class AccountProfileDialog : Window
{
    private readonly UiLocalizer locale;
    private readonly ComboBox provider = new();
    private readonly StackPanel form = new();
    private readonly TextBox copilotLogin = new();
    private readonly TextBox copilotTarget = new();
    private readonly TextBox codexEmail = new();
    private readonly TextBox codexHome = new();
    private readonly TextBox claudeLabel = new();
    private readonly TextBox claudeSnapshot = new();
    private readonly TextBlock error = new();
    private readonly IReadOnlyList<AccountProfile> configuredGithubProfiles;
    private readonly Button detectGithub = new();
    private readonly ComboBox githubCandidates = new();
    private readonly TextBlock discoveryStatus = new();
    private readonly Button detectVscode = new();
    private readonly TextBlock vscodeHelp = new();
    private readonly TextBlock detectionConditions = new();
    private bool applyingCandidate;
    private string? vscodeUserId;

    public AccountProfile? Profile { get; private set; }

    public AccountProfileDialog(UiLocalizer locale, IEnumerable<AccountProfile>? configuredGithubProfiles = null)
    {
        this.locale = locale;
        this.configuredGithubProfiles = (configuredGithubProfiles ?? Array.Empty<AccountProfile>()).ToArray();
        Title = locale.Text(UiText.AddAccount);
        Width = 480;
        SizeToContent = SizeToContent.Height;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = locale.Text(UiText.Cancel), MinWidth = 80, Margin = new Thickness(4), IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        var add = new Button { Content = locale.Text(UiText.AddAccount), MinWidth = 100, Margin = new Thickness(4), IsDefault = true };
        add.Click += (_, _) => CreateProfile();
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var content = new StackPanel();
        content.Children.Add(Label(UiText.AccountService));
        foreach (var service in Enum.GetValues<Provider>())
            provider.Items.Add(new ComboBoxItem { Content = ConnectionText.Name(service), Tag = service });
        provider.SelectedIndex = 0;
        AutomationProperties.SetName(provider, locale.Text(UiText.AccountService));
        provider.SelectionChanged += (_, _) => RenderFields();
        content.Children.Add(provider);
        content.Children.Add(form);
        error.SetResourceReference(ForegroundProperty, "WarningBrush");
        error.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        AutomationProperties.SetLiveSetting(discoveryStatus, AutomationLiveSetting.Polite);
        content.Children.Add(error);
        content.Children.Add(Label(UiText.AccountLocalStorageNote));
        root.Children.Add(content);
        detectGithub.Content = locale.Text(UiText.FindGithubAccounts);
        detectGithub.Margin = new Thickness(0, 6, 0, 4);
        AutomationProperties.SetName(detectGithub, locale.Text(UiText.FindGithubAccounts));
        AutomationProperties.SetName(githubCandidates, locale.Text(UiText.GithubCandidates));
        detectGithub.Click += async (_, _) => await DetectGithubAccountsAsync();
        githubCandidates.SelectionChanged += (_, _) =>
        {
            if (githubCandidates.SelectedItem is ComboBoxItem { Tag: GithubLoginCandidate selected })
            {
                applyingCandidate = true;
                copilotLogin.Text = selected.Login;
                copilotTarget.Clear();
                vscodeUserId = null;
                applyingCandidate = false;
            }
            else if (githubCandidates.SelectedItem is ComboBoxItem { Tag: VscodeAccountCandidate vscodeCandidate })
            {
                applyingCandidate = true;
                copilotLogin.Text = vscodeCandidate.Login;
                copilotTarget.Clear();
                vscodeUserId = vscodeCandidate.UserId;
                applyingCandidate = false;
            }
        };
        copilotLogin.TextChanged += (_, _) =>
        {
            if (applyingCandidate) return;
            vscodeUserId = null;
            githubCandidates.SelectedItem = null;
        };
        copilotTarget.TextChanged += (_, _) =>
        {
            if (!applyingCandidate && copilotTarget.Text.Length > 0) vscodeUserId = null;
        };
        detectVscode.Content = locale.Text(UiText.FindVscodeAccounts);
        detectVscode.Margin = new Thickness(0, 4, 0, 4);
        AutomationProperties.SetName(detectVscode, locale.Text(UiText.FindVscodeAccounts));
        detectVscode.Click += async (_, _) => await DetectVscodeAccountsAsync();
        vscodeHelp.Text = locale.Text(UiText.VscodeBridgeHelp);
        vscodeHelp.TextWrapping = TextWrapping.Wrap;
        vscodeHelp.Margin = new Thickness(0, 0, 0, 6);
        detectionConditions.Text = locale.Text(UiText.DetectionConditions);
        detectionConditions.TextWrapping = TextWrapping.Wrap;
        detectionConditions.Margin = new Thickness(0, 0, 0, 6);
        discoveryStatus.TextWrapping = TextWrapping.Wrap;
        Content = root;
        RenderFields();
    }

    private void RenderFields()
    {
        form.Children.Clear();
        switch ((Provider)((ComboBoxItem)provider.SelectedItem).Tag)
        {
            case Provider.Copilot:
                form.Children.Add(detectionConditions);
                form.Children.Add(detectGithub);
                form.Children.Add(Label(UiText.GithubCandidates));
                form.Children.Add(githubCandidates);
                form.Children.Add(discoveryStatus);
                AddField(UiText.GithubLoginInput, copilotLogin);
                AddField(UiText.GithubTargetInput, copilotTarget);
                break;
            case Provider.Codex:
                AddField(UiText.CodexEmailInput, codexEmail);
                if (codexHome.Text.Length == 0)
                    codexHome.Text = Environment.GetEnvironmentVariable("CODEX_HOME") ??
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                AddField(UiText.CodexHomeInput, codexHome);
                break;
            case Provider.Claude:
                AddField(UiText.ClaudeLabelInput, claudeLabel);
                AddField(UiText.ClaudeFileInput, claudeSnapshot);
                break;
        }
    }

    private async Task DetectGithubAccountsAsync()
    {
        detectGithub.IsEnabled = false;
        githubCandidates.Items.Clear();
        discoveryStatus.Text = "";
        try
        {
            var executable = OfficialCli.Resolve("gh")
                ?? throw new UsageException(FailureKind.Cli, "github_cli_missing");
            var candidates = await OfficialCli.DiscoverGithubAccountsAsync(executable,
                System.Threading.CancellationToken.None);
            foreach (var candidate in candidates.Where(candidate => !IsGithubAccountConfigured(candidate.Login)))
            {
                var name = candidate.Active
                    ? $"{candidate.Login} ({locale.Text(UiText.GithubActive)})" : candidate.Login;
                githubCandidates.Items.Add(new ComboBoxItem { Content = name, Tag = candidate });
            }
            if (githubCandidates.Items.Count == 0)
                discoveryStatus.Text = locale.Text(UiText.GithubNoNewAccounts);
        }
        catch (UsageException ex) { discoveryStatus.Text = locale.Error(ex.SafeCode); }
        catch (OperationCanceledException)
        {
            discoveryStatus.Text = locale.Text(UiText.DiscoveryTimeout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException)
        {
            discoveryStatus.Text = locale.Format(UiText.OperationFailed, ex.GetType().Name);
        }
        finally { detectGithub.IsEnabled = true; }
    }

    private async Task DetectVscodeAccountsAsync()
    {
        detectVscode.IsEnabled = false;
        githubCandidates.Items.Clear();
        discoveryStatus.Text = "";
        try
        {
            var candidates = await VscodeCopilotBridge.DiscoverAccountsAsync(
                cancellationToken: System.Threading.CancellationToken.None);
            foreach (var candidate in candidates.Where(candidate =>
                !IsGithubAccountConfigured(candidate.Login, candidate.UserId)))
            {
                githubCandidates.Items.Add(new ComboBoxItem
                {
                    Content = $"{candidate.Login} ({candidate.Label})",
                    Tag = candidate
                });
            }
            if (githubCandidates.Items.Count == 0)
                discoveryStatus.Text = locale.Text(File.Exists(Path.Combine(VscodeCopilotBridge.DefaultDirectory,
                    "accounts.json")) ? UiText.VscodeNoNewAccounts : UiText.VscodeBridgeNotInstalled);
        }
        catch (UsageException ex) { discoveryStatus.Text = locale.Error(ex.SafeCode); }
        catch (OperationCanceledException)
        {
            discoveryStatus.Text = locale.Text(UiText.DiscoveryTimeout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException)
        {
            discoveryStatus.Text = locale.Format(UiText.OperationFailed, ex.GetType().Name);
        }
        finally { detectVscode.IsEnabled = true; }
    }

    private void CreateProfile()
    {
        var selected = (Provider)((ComboBoxItem)provider.SelectedItem).Tag;
        var candidate = selected switch
        {
            Provider.Copilot => new AccountProfile
            {
                Provider = selected,
                Login = copilotLogin.Text.Trim(),
                CredentialTarget = vscodeUserId is null && !string.IsNullOrWhiteSpace(copilotTarget.Text)
                    ? copilotTarget.Text.Trim() : null,
                VscodeUserId = vscodeUserId,
                PinnedAccount = vscodeUserId is { } userId
                    ? AccountKey.FromIdentity(Provider.Copilot, "github.com:" + userId).Id
                    : null
            },
            Provider.Codex => new AccountProfile
            {
                Provider = selected,
                Email = codexEmail.Text.Trim(),
                Home = codexHome.Text.Trim()
            },
            _ => new AccountProfile
            {
                Provider = selected,
                Label = claudeLabel.Text.Trim(),
                Snapshot = claudeSnapshot.Text.Trim()
            }
        };
        try
        {
            candidate.Validate();
            Profile = candidate;
            DialogResult = true;
        }
        catch (UsageException ex)
        {
            error.Text = locale.Error(ex.SafeCode);
        }
    }

    private void AddField(UiText label, TextBox input)
    {
        form.Children.Add(Label(label));
        AutomationProperties.SetName(input, locale.Text(label));
        input.Margin = new Thickness(0, 0, 0, 6);
        form.Children.Add(input);
    }

    private bool IsGithubAccountConfigured(string login, string? userId = null)
    {
        var candidate = new AccountProfile
        {
            Provider = Provider.Copilot,
            Login = login,
            VscodeUserId = userId,
            PinnedAccount = userId is { } id
                ? AccountKey.FromIdentity(Provider.Copilot, "github.com:" + id).Id
                : null
        };
        return configuredGithubProfiles.Any(profile => profile.IsSameAccountAs(candidate));
    }

    private TextBlock Label(UiText key) => new()
    {
        Text = locale.Text(key),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 5, 0, 4)
    };
}
