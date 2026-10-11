using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public sealed class SettingsWindow : Window
{
    private readonly App app;
    private readonly ComboBox quota = new();
    private readonly TextBlock allocation = new();
    private readonly CheckBox bucketEnabled = Check("この枠の通知を有効にする");
    private readonly CheckBox daily = Check("日別目安を超えたら通知");
    private readonly CheckBox weekly = Check("週別目安を超えたら通知");
    private readonly CheckBox reset = Check("枠のリセットを通知");
    private readonly CheckBox steps = Check("使用率の刻み通知");
    private readonly TextBox stepValue = new();
    private readonly TextBox thresholds = new();
    private readonly CheckBox copilot = Check("GitHub Copilot を取得");
    private readonly TextBox login = new();
    private readonly TextBox target = new();
    private readonly CheckBox codex = Check("Codex を取得");
    private readonly TextBox email = new();
    private readonly TextBox codexHome = new();
    private readonly TextBox codexExe = new();
    private readonly CheckBox fallback = Check("公式Codex CLIによる限定フォールバック");
    private readonly CheckBox claude = Check("Claude Code の数値スナップショットを取得");
    private readonly TextBox claudeFile = new();
    private readonly TextBox claudeLabel = new();
    private readonly ComboBox theme = new();
    private readonly CheckBox autostart = Check("ログオン時にトレイで起動");
    private readonly TextBlock error = new();
    private readonly Dictionary<QuotaKey, QuotaRule> edited;
    private NotificationRules defaults;
    private QuotaKey? selected;
    private bool changing;
    private int selectedIndex;

    public SettingsWindow(App app)
    {
        this.app = app;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Title = "設定 · Usage Tray"; Width = 620; Height = 800; MinWidth = 500; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        edited = app.State.QuotaRules.ToDictionary(r => r.Key);
        defaults = app.State.Rules;
        var root = new DockPanel { Margin = new Thickness(20) };
        root.Children.Add(DockTop(new TextBlock { Text = "設定", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) }));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "キャンセル" };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = "保存" };
        save.Click += async (_, _) => await SaveAsync(save);
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        error.Foreground = (Brush)app.FindResource("WarningBrush");
        DockPanel.SetDock(error, Dock.Bottom); root.Children.Add(error);
        var tabs = new TabControl();
        tabs.Items.Add(Tab("通知", Notifications()));
        tabs.Items.Add(Tab("接続", Connections()));
        tabs.Items.Add(Tab("外観・起動", Appearance()));
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
                error.Text = ex.SafeCode;
            }
        };
        changing = true;
        quota.SelectedIndex = 0;
        changing = false;
        LoadRule();
    }

    private StackPanel Notifications()
    {
        var panel = Panel();
        panel.Children.Add(Label("設定するサービス・枠"));
        quota.Items.Add(new ComboBoxItem { Content = "既定（すべての未設定枠）" });
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
        panel.Children.Add(Label("刻み（使用率pt・1〜100）"));
        panel.Children.Add(stepValue);
        panel.Children.Add(Label("指定しきい値（%・カンマ区切り / 空欄で無効）"));
        panel.Children.Add(thresholds);
        panel.Children.Add(Label("固定配分。未使用分の繰り越し・残量による再配分はしません。\n日付は日本時間、週は月曜開始。記録がない分は未計測。\n同じ通知はアカウント・枠・期間・ルールごとに一度です。"));
        return panel;
    }

    private StackPanel Connections()
    {
        var panel = Panel();
        panel.Children.Add(copilot);
        AddField(panel, "照合するGitHubログイン名", login);
        AddField(panel, "Windows資格情報の正確なターゲット（空欄なら選択済みgh認証）", target);
        panel.Children.Add(codex);
        AddField(panel, "照合するCodexアカウントのメール", email);
        AddField(panel, "公式CLIのCODEX_HOME", codexHome);
        AddField(panel, "codex.exe（フォールバックを使用する場合）", codexExe);
        panel.Children.Add(fallback);
        panel.Children.Add(claude);
        AddField(panel, "scalar-onlyスナップショットのパス", claudeFile);
        AddField(panel, "そのスナップショットのアカウントラベル", claudeLabel);
        panel.Children.Add(Label("パス・アカウント設定のみ保存します。トークン入力・独自ログインはありません。\nCLIの認証復旧は公式CLIで行ってください。既存Claude設定は自動変更しません。"));
        return panel;
    }

    private StackPanel Appearance()
    {
        var panel = Panel();
        panel.Children.Add(Label("外観"));
        theme.Items.Add("Dark"); theme.Items.Add("Light");
        panel.Children.Add(theme);
        panel.Children.Add(autostart);
        panel.Children.Add(Label("保存時のみ現在のユーザーのログオン時起動設定を変更します。\n閉じるとトレイに残ります。終了はトレイメニューから行ってください。"));
        return panel;
    }

    private void LoadConnections()
    {
        var s = app.Settings;
        copilot.IsChecked = s.CopilotEnabled; login.Text = s.GithubLogin; target.Text = s.GithubCredentialTarget ?? "";
        codex.IsChecked = s.CodexEnabled; email.Text = s.CodexEmail; codexHome.Text = s.CodexHome;
        codexExe.Text = s.CodexExecutable; fallback.IsChecked = s.CodexCliFallback;
        claude.IsChecked = s.ClaudeEnabled; claudeFile.Text = s.ClaudeSnapshot; claudeLabel.Text = s.ClaudeAccountLabel;
        theme.SelectedItem = s.Theme; autostart.IsChecked = s.StartAtLogon;
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
            var snapshot = app.State.Latest.FirstOrDefault(s => s.Key == selected);
            var guidance = snapshot is not null ? Budgets.Calculate(snapshot) : null;
            allocation.Text = guidance is not null
                ? $"日・週の目安を自動設定（固定配分）\n1日の目安 {guidance.Daily:0.##} {MainWindow.Unit(guidance.Unit)}\n1週間の目安 {guidance.Weekly:0.##} {MainWindow.Unit(guidance.Unit)}"
                : "日・週の目安を自動設定\n総枠・期間の取得後に表示します。";
            error.Text = "";
        }
        finally { changing = false; }
    }

    private void SaveRule()
    {
        decimal Parse(string text)
        {
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                throw new UsageException(FailureKind.Schema, "通知の数値を確認してください。");
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
            var settings = new DesktopSettings
            {
                CopilotEnabled = copilot.IsChecked == true, GithubLogin = login.Text.Trim(),
                GithubCredentialTarget = string.IsNullOrWhiteSpace(target.Text) ? null : target.Text.Trim(),
                CodexEnabled = codex.IsChecked == true, CodexEmail = email.Text.Trim(),
                CodexHome = codexHome.Text.Trim(), CodexExecutable = codexExe.Text.Trim(),
                CodexCliFallback = fallback.IsChecked == true, ClaudeEnabled = claude.IsChecked == true,
                ClaudeSnapshot = claudeFile.Text.Trim(), ClaudeAccountLabel = claudeLabel.Text.Trim(),
                Theme = (string?)theme.SelectedItem ?? "Dark", StartAtLogon = autostart.IsChecked == true
            };
            await app.SaveSettingsAsync(settings, defaults, edited.Values.ToList());
            Close();
        }
        catch (UsageException ex) { error.Text = ex.SafeCode; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        { error.Text = $"設定を保存できませんでした: {ex.GetType().Name}"; }
        finally { button.IsEnabled = true; }
    }

    private static CheckBox Check(string text) => new() { Content = text };
    private static StackPanel Panel() => new() { Margin = new Thickness(12) };
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
    private static void AddField(StackPanel panel, string label, TextBox box) { panel.Children.Add(Label(label)); panel.Children.Add(box); }
    private static UIElement DockTop(UIElement element) { DockPanel.SetDock(element, Dock.Top); return element; }
    private static TabItem Tab(string name, StackPanel panel) => new()
    { Header = name, Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel } };
}
