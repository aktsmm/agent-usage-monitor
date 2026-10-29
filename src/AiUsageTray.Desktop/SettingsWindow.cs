using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using AiUsageTray.Core;
using AiUsageTray.Diagnostics;

namespace AiUsageTray.Desktop;

public sealed class SettingsWindow : Window
{
    private readonly App app;
    private UiLocalizer locale;
    private readonly List<Action> translations = [];
    private readonly ComboBox quota = new();
    private readonly TextBlock allocation = new();
    private readonly CheckBox bucketEnabled = Check("");
    private readonly CheckBox daily = Check("");
    private readonly CheckBox weekly = Check("");
    private readonly CheckBox reset = Check("");
    private readonly CheckBox steps = Check("");
    private readonly TextBox stepValue = new();
    private readonly TextBox thresholds = new();
    private readonly CheckBox copilot = Check("");
    private readonly TextBox login = new();
    private readonly TextBox target = new();
    private readonly CheckBox codex = Check("");
    private readonly TextBox email = new();
    private readonly TextBox codexHome = new();
    private readonly TextBox codexExe = new();
    private readonly CheckBox fallback = Check("");
    private readonly CheckBox claude = Check("");
    private readonly TextBox claudeFile = new();
    private readonly TextBox claudeLabel = new();
    private readonly StackPanel accountRows = Panel();
    private readonly Dictionary<string, AccountRow> accountEditors = [];
    private readonly ComboBox theme = new();
    private readonly CheckBox autostart = Check("");
    private readonly CheckBox autoUpdate = Check("");
    private readonly CheckBox strip = Check("");
    private readonly ComboBox language = new();
    private bool stripEdited;
    private readonly TextBlock updateStatus = new();
    private readonly TextBlock unsaved = new();
    private readonly StackPanel migration = Panel();
    private readonly Dictionary<Provider, TextBlock> connectionStatus = [];
    private readonly Dictionary<Provider, TextBlock> candidateStatus = [];
    private readonly Dictionary<Provider, Button> detectButtons = [];
    private readonly Dictionary<Provider, Button> useButtons = [];
    private readonly Button cancelDetection = new();
    private DesktopSettings draft;
    private bool ready;
    private bool dirty;
    private readonly HashSet<Provider> adopted = [];
    private readonly TextBlock error = new();
    private readonly Dictionary<QuotaKey, QuotaRule> edited;
    private NotificationRules defaults;
    private QuotaKey? selected;
    private bool changing;
    private int selectedIndex;
    private sealed record AccountRow(TextBox Alias, CheckBox Visible, CheckBox InStrip, string DefaultName);

    public SettingsWindow(App app, bool showAppearance = false)
    {
        this.app = app;
        locale = app.Localizer;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AiUsageTray.Desktop;component/HistoryControls.xaml", UriKind.Relative)
        });
        foreach (var (control, key) in new[]
        {
            (bucketEnabled, UiText.EnableQuotaNotifications), (daily, UiText.NotifyDaily),
            (weekly, UiText.NotifyWeekly), (reset, UiText.NotifyReset), (steps, UiText.NotifySteps),
            (copilot, UiText.FetchCopilot), (codex, UiText.FetchCodex), (claude, UiText.FetchClaude),
            (fallback, UiText.CodexFallback), (autostart, UiText.StartAtLogon),
            (autoUpdate, UiText.AutoUpdateOption), (strip, UiText.ShowUsageStrip)
        }) translations.Add(() => control.Content = locale.Text(key));
        translations.Add(() => { Title = locale.Text(UiText.SettingsTitle); cancelDetection.Content = locale.Text(UiText.CancelDetection); });
        draft = app.Settings;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Width = 650; Height = 800; MinWidth = 500; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        edited = app.State.QuotaRules.ToDictionary(r => r.Key);
        defaults = app.State.Rules;
        var root = new DockPanel { Margin = new Thickness(20) };
        var heading = Label(UiText.Settings);
        heading.FontSize = 22; heading.FontWeight = FontWeights.SemiBold;
        root.Children.Add(DockTop(heading));
        unsaved.Foreground = (Brush)app.FindResource("WarningBrush");
        root.Children.Add(DockTop(unsaved));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button(UiText.Cancel);
        cancel.Click += (_, _) => Close();
        var save = Button(UiText.Save);
        save.Click += async (_, _) => await SaveAsync(save);
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        error.Foreground = (Brush)app.FindResource("WarningBrush");
        DockPanel.SetDock(error, Dock.Bottom); root.Children.Add(error);
        var tabs = new TabControl();
        tabs.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        tabs.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        tabs.Items.Add(Tab(UiText.Connections, Connections()));
        tabs.Items.Add(Tab(UiText.Notifications, Notifications()));
        tabs.Items.Add(Tab(UiText.AppearanceLanguage, Appearance()));
        var historySettings = Panel();
        var historyButton = Button(UiText.HistoryStorage);
        historyButton.Click += (_, _) => app.OpenHistory();
        historySettings.Children.Add(historyButton);
        historySettings.Children.Add(Label(UiText.StorageNotes));
        tabs.Items.Add(Tab(UiText.HistoryStorage, historySettings));
        if (showAppearance) tabs.SelectedIndex = 2;
        root.Children.Add(tabs);
        Content = root;
        LoadConnections();
        quota.SelectionChanged += (_, _) =>
        {
            if (changing) return;
            try { SaveRule(); LoadRule(); }
            catch (UsageException ex)
            {
                changing = true;
                quota.SelectedIndex = selectedIndex;
                changing = false;
                error.Text = locale.Error(ex.SafeCode);
            }
        };
        changing = true;
        quota.SelectedIndex = 0;
        changing = false;
        LoadRule();
        root.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => MarkDirty()));
        root.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((_, _) => MarkDirty()));
        root.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((_, _) => MarkDirty()));
        theme.SelectionChanged += (_, _) => MarkDirty();
        language.SelectionChanged += (_, _) =>
        {
            if (!ready || changing) return;
            draft = ReadAccountPreferences(draft);
            locale = new UiLocalizer((DisplayLanguage)((ComboBoxItem)language.SelectedItem).Tag);
            foreach (var apply in translations) apply();
            LoadAccountRows(draft);
            MarkDirty(); RenderStatus(); RenderAllocation();
        };
        strip.Checked += (_, _) => { if (ready && !changing) stripEdited = true; };
        strip.Unchecked += (_, _) => { if (ready && !changing) stripEdited = true; };
        Closing += async (_, e) =>
        {
            if (!dirty) return;
            var answer = MessageBox.Show(this, locale.Text(UiText.UnsavedConfirm),
                locale.Text(UiText.AgentUsage), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (answer == MessageBoxResult.No) { dirty = false; return; }
            e.Cancel = true;
            await SaveAsync(save);
        };
        ready = true;
        foreach (var apply in translations) apply();
        RenderStatus();
    }

    private void MarkDirty()
    {
        if (!ready || changing) return;
        dirty = true;
        unsaved.Text = locale.Text(UiText.UnsavedChanges);
    }

    private StackPanel Notifications()
    {
        var panel = Panel();
        panel.Children.Add(Label(UiText.NotificationQuota));
        var defaultQuota = new ComboBoxItem();
        translations.Add(() => defaultQuota.Content = locale.Text(UiText.DefaultQuota));
        quota.Items.Add(defaultQuota);
        foreach (var snapshot in app.State.Latest)
            quota.Items.Add(new ComboBoxItem { Content = $"{snapshot.Key.Account.Provider} · {snapshot.Key.Bucket}", Tag = snapshot.Key });
        panel.Children.Add(quota);
        allocation.Margin = new Thickness(0, 12, 0, 12);
        panel.Children.Add(new Border { Background = (Brush)app.FindResource("SoftBrush"),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = allocation });
        panel.Children.Add(bucketEnabled);
        panel.Children.Add(daily);
        panel.Children.Add(weekly);
        panel.Children.Add(reset);
        panel.Children.Add(steps);
        panel.Children.Add(Label(UiText.StepInput));
        panel.Children.Add(stepValue);
        panel.Children.Add(Label(UiText.ThresholdInput));
        panel.Children.Add(thresholds);
        panel.Children.Add(Label(UiText.NotificationNotes));
        return panel;
    }

    private StackPanel Connections()
    {
        var panel = Panel();
        panel.Children.Add(Label(UiText.ConnectionIntro));
        panel.Children.Add(Label(UiText.AccountsHeading));
        panel.Children.Add(Label(UiText.AccountsNote));
        accountRows.Children.Clear();
        panel.Children.Add(accountRows);
        var addAccount = Button(UiText.AddAccount);
        addAccount.Click += (_, _) => AddAccount();
        panel.Children.Add(addAccount);
        panel.Children.Add(Label(UiText.CandidateNote));
        migration.Children.Add(Label(UiText.LegacyConnectionNote));
        var migrate = Button(UiText.LegacyEnable);
        migrate.Click += (_, _) =>
        {
            foreach (var provider in Enum.GetValues<Provider>())
                if (draft.Mode(provider) == ConnectionMode.NeedsConfirmation)
                {
                    draft = draft.WithMode(provider, ConnectionMode.Pending);
                    adopted.Add(provider);
                }
            MarkDirty(); RenderStatus();
        };
        var keep = Button(UiText.LegacyKeep);
        keep.Click += (_, _) =>
        {
            foreach (var provider in Enum.GetValues<Provider>())
                if (draft.Mode(provider) == ConnectionMode.NeedsConfirmation)
                {
                    draft = draft.WithMode(provider, ConnectionMode.Disabled);
                    adopted.Add(provider);
                }
            MarkDirty(); RenderStatus();
        };
        migration.Children.Add(migrate); migration.Children.Add(keep);
        panel.Children.Add(migration);
        foreach (var provider in Enum.GetValues<Provider>())
        {
            var card = Panel();
            card.Children.Add(new TextBlock { Text = ConnectionText.Name(provider), FontSize = 17, FontWeight = FontWeights.SemiBold });
            var status = Label("");
            connectionStatus[provider] = status;
            card.Children.Add(status);
            card.Children.Add(provider switch { Provider.Copilot => copilot, Provider.Codex => codex, _ => claude });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var detect = Button(provider == Provider.Claude ? UiText.CheckState : UiText.DetectAgain);
            detect.Click += async (_, _) => await ActionAsync("detect", provider);
            detectButtons[provider] = detect;
            buttons.Children.Add(detect);
            var use = Button(UiText.UseCandidate);
            use.Click += (_, _) =>
            {
                if (!app.Settings.ConnectionResults.TryGetValue(provider, out var probe)) return;
                var identity = provider switch { Provider.Copilot => draft.GithubLogin, Provider.Codex => draft.CodexEmail, _ => "" };
                if (!string.IsNullOrWhiteSpace(identity) &&
                    MessageBox.Show(this, locale.Format(UiText.CandidateConfirm, identity, probe.Identity),
                        locale.Text(UiText.AgentUsage), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                draft = ConnectionEdits.Apply(draft, ReadFields());
                draft = draft.ApplyProbe(probe);
                adopted.Add(provider);
                LoadConnections(); MarkDirty(); RenderStatus();
            };
            useButtons[provider] = use;
            buttons.Children.Add(use);
            card.Children.Add(buttons);
            candidateStatus[provider] = Label("");
            card.Children.Add(candidateStatus[provider]);
            var details = Panel();
            if (provider == Provider.Copilot)
            {
                AddField(details, UiText.GithubLoginInput, login);
                AddField(details, UiText.GithubTargetInput, target);
            }
            else if (provider == Provider.Codex)
            {
                AddField(details, UiText.CodexEmailInput, email);
                AddField(details, UiText.CodexHomeInput, codexHome);
                AddField(details, UiText.CodexExecutableInput, codexExe);
                details.Children.Add(fallback);
                details.Children.Add(Label(UiText.CodexFallbackNote));
            }
            else
            {
                AddField(details, UiText.ClaudeFileInput, claudeFile);
                AddField(details, UiText.ClaudeLabelInput, claudeLabel);
                details.Children.Add(Label(UiText.ClaudeNote));
            }
            var expander = new Expander { Content = details };
            translations.Add(() => expander.Header = locale.Text(UiText.ConnectionAdvanced));
            card.Children.Add(expander);
            panel.Children.Add(new Border { Child = card, CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1), BorderBrush = (Brush)app.FindResource("BorderBrush"),
                Background = (Brush)app.FindResource("SoftBrush"), Margin = new Thickness(0, 8, 0, 8) });
        }
        cancelDetection.Click += async (_, _) => await ActionAsync("detect-cancel");
        panel.Children.Add(cancelDetection);
        panel.Children.Add(Label(UiText.CredentialNotes));
        return panel;
    }

    private StackPanel Appearance()
    {
        var panel = Panel();
        panel.Children.Add(Label(UiText.Appearance));
        foreach (var (value, key) in new[] { ("Dark", UiText.Dark), ("Light", UiText.Light) })
        {
            var item = new ComboBoxItem { Tag = value };
            translations.Add(() => item.Content = locale.Text(key));
            theme.Items.Add(item);
        }
        panel.Children.Add(theme);
        panel.Children.Add(Label(UiText.DisplayLanguage));
        foreach (var (value, key) in new[] { (DisplayLanguage.System, UiText.FollowSystem),
            (DisplayLanguage.Japanese, UiText.Japanese), (DisplayLanguage.English, UiText.English) })
        {
            var item = new ComboBoxItem { Tag = value };
            translations.Add(() => item.Content = locale.Text(key));
            language.Items.Add(item);
        }
        panel.Children.Add(language);
        panel.Children.Add(Label(UiText.LanguageExplanation));
        panel.Children.Add(Label(UiText.LanguageInvariantNote));
        panel.Children.Add(strip);
        panel.Children.Add(Label(UiText.UsageStripNotes));
        panel.Children.Add(autostart);
        panel.Children.Add(Label(UiText.StartupNote));
        panel.Children.Add(Label(UiText.UpdateTitle));
        panel.Children.Add(autoUpdate);
        panel.Children.Add(Label(UiText.UpdateBehavior));
        panel.Children.Add(updateStatus);
        var check = Button(UiText.UpdateCheck);
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Click += async (_, _) => await ActionAsync("update-check");
        panel.Children.Add(check);
        var versionNote = Label("");
        translations.Add(() => versionNote.Text = locale.Format(UiText.UpdateVersionNote, UpdateFlow.Version, UpdateFlow.ReleasePage));
        panel.Children.Add(versionNote);
        return panel;
    }

    private void LoadConnections()
    {
        bool previous = changing;
        changing = true;
        var s = draft;
        LoadAccountRows(s);
        copilot.IsChecked = s.CopilotEnabled; login.Text = s.GithubLogin; target.Text = s.GithubCredentialTarget ?? "";
        codex.IsChecked = s.CodexEnabled; email.Text = s.CodexEmail; codexHome.Text = s.CodexHome;
        codexExe.Text = s.CodexExecutable; fallback.IsChecked = s.CodexCliFallback;
        claude.IsChecked = s.ClaudeEnabled; claudeFile.Text = s.ClaudeSnapshot; claudeLabel.Text = s.ClaudeAccountLabel;
        theme.SelectedItem = theme.Items.OfType<ComboBoxItem>().Single(i => (string)i.Tag == s.Theme);
        language.SelectedItem = language.Items.OfType<ComboBoxItem>().Single(i => (DisplayLanguage)i.Tag == s.Language);
        strip.IsChecked = s.UsageStripEnabled;
        autostart.IsChecked = s.StartAtLogon;
        autoUpdate.IsChecked = s.AutoUpdateEnabled;
        changing = previous;
    }

    private void LoadAccountRows(DesktopSettings settings)
    {
        accountRows.Children.Clear();
        accountEditors.Clear();
        var header = new Grid { Margin = new Thickness(0, 8, 0, 3) };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(78) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(105) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        foreach (var (text, column) in new[] { (UiText.Service, 0), (UiText.AccountIdentity, 1),
                     (UiText.AccountName, 2), (UiText.AccountShow, 3), (UiText.AccountStrip, 4) })
        {
            var label = Label(locale.Text(text));
            label.FontSize = 11;
            label.FontWeight = FontWeights.SemiBold;
            Grid.SetColumn(label, column);
            header.Children.Add(label);
        }
        accountRows.Children.Add(header);
        foreach (var entry in settings.Accounts())
        {
            var profile = entry.Profile;
            var preference = entry.Preference;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(105) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var service = Label(ConnectionText.Name(profile.Provider));
            service.FontSize = 11;
            Grid.SetColumn(service, 0);
            row.Children.Add(service);
            var account = Label(AccountNames.Auto(profile) + (entry.IsPrimary ? " · " + locale.Text(UiText.PrimaryAccount) : ""));
            account.FontSize = 11;
            account.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(account, 1);
            row.Children.Add(account);
            var rowName = ConnectionText.Name(profile.Provider) + " " + entry.Name;
            var alias = new TextBox { Text = preference.Alias ?? entry.Name, ToolTip = entry.Name, Margin = new Thickness(2) };
            AutomationProperties.SetName(alias, locale.Text(UiText.AccountName) + ": " + rowName);
            alias.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
            alias.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            Grid.SetColumn(alias, 2);
            row.Children.Add(alias);
            var visible = Check("");
            visible.IsChecked = preference.Visible;
            visible.ToolTip = locale.Text(UiText.AccountShow);
            AutomationProperties.SetName(visible, locale.Text(UiText.AccountShow) + ": " + rowName);
            Grid.SetColumn(visible, 3);
            row.Children.Add(visible);
            var inStrip = Check("");
            inStrip.IsChecked = preference.ShowInStrip;
            inStrip.ToolTip = locale.Text(UiText.AccountStrip);
            AutomationProperties.SetName(inStrip, locale.Text(UiText.AccountStrip) + ": " + rowName);
            Grid.SetColumn(inStrip, 4);
            row.Children.Add(inStrip);
            accountEditors[profile.Id] = new(alias, visible, inStrip, entry.Name);
            accountRows.Children.Add(row);
            if (!entry.IsPrimary)
            {
                var removeText = locale.Text(UiText.RemoveAccount);
                var remove = new Button { Content = removeText };
                AutomationProperties.SetName(remove, removeText + ": " + rowName);
                remove.HorizontalAlignment = HorizontalAlignment.Right;
                remove.Click += (_, _) =>
                {
                    if (MessageBox.Show(this, locale.Format(UiText.RemoveAccountConfirm, rowName),
                        locale.Text(UiText.AgentUsage), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                    draft = ReadAccountPreferences(draft).RemoveAccount(profile.Id);
                    LoadAccountRows(draft);
                    MarkDirty();
                };
                accountRows.Children.Add(remove);
            }
        }
    }

    private DesktopSettings ReadAccountPreferences(DesktopSettings settings)
    {
        foreach (var entry in settings.Accounts())
            if (accountEditors.TryGetValue(entry.Profile.Id, out var editor))
                settings = settings.WithPreference(entry.Profile.Id, new AccountPreference
                {
                    Alias = string.Equals(editor.Alias.Text.Trim(), editor.DefaultName, StringComparison.Ordinal)
                        ? null : editor.Alias.Text,
                    Visible = editor.Visible.IsChecked == true,
                    ShowInStrip = editor.InStrip.IsChecked == true
                });
        return settings;
    }

    private void AddAccount()
    {
        var editor = new AccountProfileDialog(locale, draft.Profiles()) { Owner = this };
        if (editor.ShowDialog() != true || editor.Profile is not { } profile) return;
        if (draft.Profiles().Any(existing => existing.IsSameAccountAs(profile)))
        {
            error.Text = locale.Text(UiText.AccountAlreadyConfigured);
            return;
        }
        try
        {
            draft = ReadAccountPreferences(draft).AddAccount(profile);
            LoadAccountRows(draft);
            MarkDirty();
        }
        catch (UsageException ex) { error.Text = locale.Error(ex.SafeCode); }
    }

    public void RenderStatus()
    {
        if (!dirty)
        {
            draft = app.Settings;
            LoadConnections();
        }
        migration.Visibility = Enum.GetValues<Provider>().Any(p => draft.Mode(p) == ConnectionMode.NeedsConfirmation)
            ? Visibility.Visible : Visibility.Collapsed;
        foreach (var provider in Enum.GetValues<Provider>())
        {
            connectionStatus[provider].Text = ConnectionText.Summary(draft, provider,
                app.LastFailures.GetValueOrDefault(provider), app.Discovering, locale);
            var savedIdentity = provider switch { Provider.Copilot => draft.GithubLogin,
                Provider.Codex => draft.CodexEmail, _ => draft.ClaudeAccountLabel };
            if (!string.IsNullOrWhiteSpace(savedIdentity)) connectionStatus[provider].Text += "\n" + locale.Format(UiText.SavedAccount, savedIdentity);
            bool hasProbe = app.Settings.ConnectionResults.TryGetValue(provider, out var probe);
            candidateStatus[provider].Text = hasProbe
                ? ConnectionText.Probe(probe!, locale) + "\n" + locale.Format(UiText.CheckedAt,
                    probe!.CheckedAt.ToOffset(AiUsageTray.Core.JapaneseCalendar.Offset).ToString("g", locale.Culture)) +
                    (probe.Identity.Length > 0 ? "\n" + locale.Format(UiText.DetectedCandidate, probe.Identity) : "") +
                    (probe.Version.Length > 0 ? "\nCLI: " + probe.Version : "") : "";
            useButtons[provider].Visibility = hasProbe && probe!.Status == DiscoveryStatus.Ready &&
                (draft.PinnedAccount(provider) != probe.AccountKey || draft.Mode(provider) != ConnectionMode.Automatic)
                ? Visibility.Visible : Visibility.Collapsed;
            useButtons[provider].IsEnabled = !app.Discovering;
            detectButtons[provider].IsEnabled = !app.Discovering && !app.IsDemo;
        }
        cancelDetection.Visibility = app.Discovering ? Visibility.Visible : Visibility.Collapsed;
        updateStatus.Text = app.Update?.Display(locale) ?? locale.Text(UiText.UpdateIdle);
    }

    private async Task ActionAsync(string method, Provider? provider = null)
    {
        try { await app.ConnectionActionAsync(method, provider); error.Text = ""; }
        catch (Exception ex) when (ex is UsageException or IOException or JsonException or OperationCanceledException)
        { error.Text = ex is UsageException usage ? locale.Error(usage.SafeCode) : locale.Format(UiText.OperationFailed, ex.GetType().Name); }
    }

    private void LoadRule()
    {
        changing = true;
        try
        {
            selected = (quota.SelectedItem as ComboBoxItem)?.Tag as QuotaKey;
            selectedIndex = quota.SelectedIndex;
            var entry = selected is { } key ? edited.GetValueOrDefault(key) : null;
            var rule = entry?.Rules ?? defaults;
            bucketEnabled.IsEnabled = selected is not null;
            bucketEnabled.IsChecked = entry?.Enabled ?? true;
            daily.IsChecked = rule.DailyGuidance; weekly.IsChecked = rule.WeeklyGuidance; reset.IsChecked = rule.Reset;
            steps.IsChecked = rule.PercentageStep is not null; stepValue.Text = (rule.PercentageStep ?? 10).ToString(CultureInfo.InvariantCulture);
            thresholds.Text = string.Join(", ", rule.Thresholds.Select(x => x.ToString(CultureInfo.InvariantCulture)));
            RenderAllocation();
            error.Text = "";
        }
        finally { changing = false; }
    }

    private void RenderAllocation()
    {
        var snapshot = app.State.Latest.FirstOrDefault(s => s.Key == selected);
        var guidance = snapshot is not null ? Budgets.Calculate(snapshot) : null;
        allocation.Text = guidance is not null
            ? locale.Format(UiText.AutoGuidance, guidance.Daily, locale.Unit(guidance.Unit), guidance.Weekly)
            : locale.Text(UiText.AutoGuidanceUnknown);
    }

    private void SaveRule()
    {
        decimal Parse(string text)
        {
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                throw new UsageException(FailureKind.Schema, "settings_notification_number_invalid");
            return value;
        }
        var rule = new NotificationRules
        {
            DailyGuidance = daily.IsChecked == true, WeeklyGuidance = weekly.IsChecked == true,
            Reset = reset.IsChecked == true, PercentageStep = steps.IsChecked == true ? Parse(stepValue.Text) : null,
            Thresholds = thresholds.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Parse).ToArray()
        };
        rule.Validate();
        if (selected is { } key) edited[key] = new(key, rule, bucketEnabled.IsChecked == true);
        else defaults = rule;
    }

    private async Task SaveAsync(Button button)
    {
        button.IsEnabled = false;
        try
        {
            SaveRule();
            var settings = ReadAccountPreferences(ConnectionEdits.Apply(draft, ReadFields()));
            foreach (var provider in Enum.GetValues<Provider>())
                if (!adopted.Contains(provider) && settings.SelectionId(provider) == draft.SelectionId(provider) &&
                    settings.Mode(provider) == draft.Mode(provider) &&
                    (provider != Provider.Codex || settings.CodexExecutable == draft.CodexExecutable &&
                        settings.CodexCliFallback == draft.CodexCliFallback))
                    settings = ConnectionEdits.CopyProvider(app.Settings, settings, provider);
            await app.SaveSettingsAsync(settings, defaults, edited.Values.ToList(), stripEdited);
            dirty = false;
            Close();
        }
        catch (UsageException ex) { error.Text = locale.Error(ex.SafeCode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        { error.Text = locale.Format(UiText.SaveFailed, ex.GetType().Name); }
        finally { button.IsEnabled = true; }
    }

    private DesktopSettings ReadFields() => draft with
    {
        CopilotEnabled = copilot.IsChecked == true, GithubLogin = login.Text.Trim(),
        GithubCredentialTarget = string.IsNullOrWhiteSpace(target.Text) ? null : target.Text.Trim(),
        CodexEnabled = codex.IsChecked == true, CodexEmail = email.Text.Trim(),
        CodexHome = codexHome.Text.Trim(), CodexExecutable = codexExe.Text.Trim(),
        CodexCliFallback = fallback.IsChecked == true, ClaudeEnabled = claude.IsChecked == true,
        ClaudeSnapshot = claudeFile.Text.Trim(), ClaudeAccountLabel = claudeLabel.Text.Trim(),
        Theme = (string)((ComboBoxItem)theme.SelectedItem).Tag, StartAtLogon = autostart.IsChecked == true,
        Language = (DisplayLanguage)((ComboBoxItem)language.SelectedItem).Tag,
        UsageStripEnabled = strip.IsChecked == true, AutoUpdateEnabled = autoUpdate.IsChecked == true
    };

    private static CheckBox Check(string text) => new() { Content = text };
    private static StackPanel Panel() => new() { Margin = new Thickness(12) };
    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return label;
    }
    private TextBlock Label(UiText key)
    {
        var label = Label("");
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        translations.Add(() => label.Text = locale.Text(key));
        return label;
    }
    private Button Button(UiText key)
    {
        var button = new Button();
        translations.Add(() => { button.Content = locale.Text(key); AutomationProperties.SetName(button, locale.Text(key)); });
        return button;
    }
    private void AddField(StackPanel panel, UiText key, TextBox box)
    {
        panel.Children.Add(Label(key)); panel.Children.Add(box);
        translations.Add(() => AutomationProperties.SetName(box, locale.Text(key)));
    }
    private static UIElement DockTop(UIElement element) { DockPanel.SetDock(element, Dock.Top); return element; }
    private TabItem Tab(UiText key, StackPanel panel)
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        scroll.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        scroll.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        var tab = new TabItem { Content = scroll };
        translations.Add(() => tab.Header = locale.Text(key));
        return tab;
    }
}
