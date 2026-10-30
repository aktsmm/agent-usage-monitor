using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public partial class MainWindow : Window
{
    private readonly App app;
    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        Closed += (_, _) => app.OverviewClosed(this);
        Activated += (_, _) => Render();
        ContentRendered += (_, _) => Render();
    }

    private void RefreshClicked(object sender, RoutedEventArgs e) => app.RequestRefresh();
    private void SettingsClicked(object sender, RoutedEventArgs e) => app.OpenSettings();
    private void HistoryClicked(object sender, RoutedEventArgs e) => app.OpenHistory();

    public void Render()
    {
        if (!IsVisible) return;
        var locale = app.Localizer;
        Cards.Children.Clear();
        foreach (var provider in Enum.GetValues<Provider>())
        {
            var content = new StackPanel();
            var failure = app.LastFailures.GetValueOrDefault(provider);
            var accounts = app.Settings.Accounts().Where(a => a.Profile.Provider == provider &&
                a.Profile.Enabled && a.Preference.Visible).ToArray();
            var snapshots = failure?.Kind == FailureKind.AccountMismatch ? [] :
                app.State.Latest.Where(s => s.Key.Account.Provider == provider).ToArray();
            if (accounts.Length == 0) snapshots = [];
            else if (accounts.Length > 1)
            {
                var ids = accounts.Select(a => a.Profile.ExpectedAccount).Where(id => id is not null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                snapshots = snapshots.Where(s => ids.Contains(s.Key.Account.Id)).ToArray();
            }
            // Hide unlimited buckets only inside an account that also has limited ones; an all-unlimited account must stay visible.
            snapshots = QuotaVisibility.HideUnlimitedPerAccount(snapshots).ToArray();
            content.Children.Add(Label(ConnectionText.Name(provider), 16, true));
            if (accounts.Length == 1)
            {
                var identity = accounts[0].Profile.Identity;
                if (!string.IsNullOrWhiteSpace(identity)) content.Children.Add(Label(identity, 11));
            }
            if (snapshots.Length == 0)
            {
                content.Children.Add(Label(ConnectionText.Summary(app.Settings, provider, failure, app.Discovering, locale), 12));
            }
            var accountGroups = snapshots.GroupBy(s => s.Key.Account.Id).ToArray();
            foreach (var accountGroup in accountGroups)
            {
                if (accounts.Length > 1)
                {
                    var account = accounts.FirstOrDefault(a => string.Equals(a.Profile.ExpectedAccount,
                        accountGroup.Key, StringComparison.OrdinalIgnoreCase));
                    content.Children.Add(Label(account?.Name ?? accountGroup.Key[..Math.Min(8, accountGroup.Key.Length)],
                        13, true));
                }
                foreach (var snapshot in accountGroup)
                {
                bool stale = !snapshot.IsFresh(DateTimeOffset.UtcNow, app.State.Polling.MaximumAge);
                content.Children.Add(Label(locale.Text(snapshot.Window.Kind switch { WindowKind.Short => UiText.ShortQuota,
                    WindowKind.Weekly => UiText.WeeklyQuota, WindowKind.Monthly => UiText.MonthlyQuota, _ => UiText.UnknownQuotaWindow }), 12, true));
                content.Children.Add(Label(locale.Text(stale ? UiText.Stale : failure is null ? UiText.Fetched : UiText.LastConfirmed)
                    + " · " + snapshot.ObservedAt.ToOffset(JapaneseCalendar.Offset).ToString("g", locale.Culture) + " JST", 11));
                var percent = snapshot.UsedPercent;
                content.Children.Add(Label(snapshot.Unlimited ? locale.Text(UiText.Unlimited)
                    : percent is { } used ? locale.Format(UiText.UsedRemaining, used, 100 - used)
                    : snapshot.Used is { } scalar ? locale.Format(UiText.ScalarWithoutDenominator, scalar, locale.Unit(snapshot.Unit))
                    : locale.Text(UiText.UnknownUsage), 21, true));
                if (percent is { } p)
                    content.Children.Add(new ProgressBar { Value = (double)p });
                content.Children.Add(Label(snapshot.Window.ResetsAt is { } reset
                    ? locale.Format(UiText.ResetAt, reset.ToOffset(JapaneseCalendar.Offset).ToString("g", locale.Culture))
                    : locale.Text(UiText.ResetUnknown), 11));
                if (Budgets.Calculate(snapshot) is { } guidance)
                {
                    var budgets = new Grid { Margin = new Thickness(0, 12, 0, 4) };
                    budgets.ColumnDefinitions.Add(new());
                    bool showWeekly = snapshot.Window.Kind == WindowKind.Monthly;
                    if (showWeekly) budgets.ColumnDefinitions.Add(new());
                    foreach (bool weekly in showWeekly ? new[] { false, true } : new[] { false })
                    {
                        var summary = app.Consumption(snapshot, weekly);
                        var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                        string label = locale.Text(weekly ? UiText.Weekly : UiText.Daily);
                        var ratio = summary.Measured is { } amount && summary.Guidance is > 0
                            ? amount / summary.Guidance.Value * 100 : (decimal?)null;
                        cell.Children.Add(Label(ratio > 100 ? locale.Format(UiText.GuidanceExceedance, label) : label, 12, true));
                        cell.Children.Add(Label(ratio is { } value ? locale.Format(UiText.GuidanceUsage,
                            value, locale.Coverage(summary.Coverage)) : locale.Text(UiText.Unmeasured), 11));
                        if (ratio is { } progress) cell.Children.Add(new ProgressBar { Value = (double)Math.Min(progress, 100) });
                        cell.Children.Add(Label(locale.Format(UiText.ConsumptionOfGuidance,
                            summary.Measured?.ToString("0.##", locale.Culture) ?? "—",
                            summary.Guidance?.ToString("0.##", locale.Culture) ?? "—", locale.Unit(snapshot.Unit)), 11));
                        Grid.SetColumn(cell, weekly ? 1 : 0);
                        budgets.Children.Add(cell);
                    }
                    content.Children.Add(budgets);
                    content.Children.Add(Label(locale.Format(UiText.FixedAllocation, guidance.Daily, guidance.Weekly, locale.Unit(guidance.Unit)), 11));
                }
                else content.Children.Add(Label(locale.Text(UiText.GuidanceUnknown), 11));
                var details = new StackPanel();
                details.Children.Add(Label($"{snapshot.Plan ?? locale.Text(UiText.PlanUnknown)} · {snapshot.Key.Bucket} · {snapshot.Source}", 11));
                if (snapshot.CreditsUsed is { } credits)
                    details.Children.Add(Label(locale.Format(UiText.CreditsApiValue, credits), 11));
                content.Children.Add(new Expander { Header = locale.Text(UiText.QuotaDetails), Content = details });
                }
            }
            if (failure is not null)
                content.Children.Add(new TextBlock { Text = ConnectionText.Failure(failure, locale), Foreground = (Brush)FindResource("WarningBrush") });
            Cards.Children.Add(new Border
            {
                Background = (Brush)FindResource("SoftBrush"), BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Child = content
            });
        }
        Footer.Text = app.IsDemo ? locale.Text(UiText.DemoFooter)
            : locale.Format(UiText.PollingFooter, app.State.Polling.Interval.TotalMinutes) + "\n"
                + locale.Text(UiText.CalendarExplanation) + "\n" + UpdateFlow.Version + " · " + app.Update?.Display(locale);
        Error.Text = app.LastError is { } code ? locale.Error(code) : "";
        if (app.HistoryStatus is { Saved: false } pending)
            Error.Text += "\n" + locale.Format(UiText.PendingRecords, pending.PendingRecords,
                pending.Error is null ? "" : locale.Error(pending.Error));
    }

    private TextBlock Label(string text, double size = 12, bool bold = false) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = (Brush)FindResource(bold ? "TextBrush" : "MutedBrush"),
        Margin = new Thickness(0, 3, 0, 3)
    };
    internal static string Unit(UsageUnit unit) => unit switch
    { UsageUnit.PercentagePoints => "pt", UsageUnit.Credits => "クレジット", UsageUnit.Requests => "回", _ => "単位不明" };
}
