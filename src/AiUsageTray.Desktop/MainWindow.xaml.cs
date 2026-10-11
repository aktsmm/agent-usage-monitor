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
    }

    private void RefreshClicked(object sender, RoutedEventArgs e) => app.RequestRefresh();
    private void SettingsClicked(object sender, RoutedEventArgs e) => app.OpenSettings();

    public void Render()
    {
        if (!IsVisible) return;
        Cards.Children.Clear();
        foreach (var provider in Enum.GetValues<Provider>())
        {
            var content = new StackPanel();
            var snapshots = app.State.Latest.Where(s => s.Key.Account.Provider == provider).ToArray();
            if (snapshots.Any(s => !s.Unlimited))
                snapshots = snapshots.Where(s => !s.Unlimited).ToArray();
            var failure = app.LastFailures.GetValueOrDefault(provider);
            content.Children.Add(Label(provider switch { Provider.Copilot => "GitHub Copilot", Provider.Claude => "Claude Code", _ => "Codex" }, 16, true));
            if (snapshots.Length == 0)
            {
                content.Children.Add(Label(provider == Provider.Claude
                    ? "未取得 · CLI は導入済み。認証と数値連携が必要です。"
                    : "未取得 · 設定で対象アカウントを指定してください。", 12));
            }
            foreach (var snapshot in snapshots)
            {
                bool stale = !snapshot.IsFresh(DateTimeOffset.UtcNow, app.State.Polling.MaximumAge);
                content.Children.Add(Label($"{snapshot.Plan ?? "プラン未確認"} · {snapshot.Key.Bucket} · {snapshot.Source}", 11));
                content.Children.Add(Label($"{(stale ? "古い値" : failure is null ? "取得済み" : "最終確認値")} · {snapshot.ObservedAt.ToOffset(JapaneseCalendar.Offset):M/d HH:mm}", 11));
                var percent = snapshot.UsedPercent;
                content.Children.Add(Label(snapshot.Unlimited ? "上限なし（API報告）"
                    : percent is { } used ? $"使用済み {used:0.##}% · 残り {100 - used:0.##}%"
                    : snapshot.Used is { } scalar ? $"{scalar:0.##} {Unit(snapshot.Unit)} · 分母未取得"
                    : "使用量不明", 21, true));
                if (percent is { } p)
                    content.Children.Add(new ProgressBar { Value = (double)p });
                if (snapshot.CreditsUsed is { } credits)
                    content.Children.Add(Label($"credits_used（API値）{credits:0.##} · 単位換算・枠の合算なし", 11));
                content.Children.Add(Label(snapshot.Window.ResetsAt is { } reset
                    ? $"リセット {reset.ToOffset(JapaneseCalendar.Offset):M/d HH:mm} JST" : "リセット日時不明", 11));
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
                        string label = weekly ? "今週" : "今日";
                        var ratio = summary.Measured is { } amount && summary.Guidance is > 0
                            ? amount / summary.Guidance.Value * 100 : (decimal?)null;
                        cell.Children.Add(Label(label + (ratio > 100 ? $" · {(weekly ? "今週" : "今日")}の目安を超過" : ""), 12, true));
                        cell.Children.Add(Label(ratio is { } value ? $"目安の{value:0}%（{CoverageLabel(summary.Coverage)}）" : "未計測", 11));
                        if (ratio is { } progress) cell.Children.Add(new ProgressBar { Value = (double)Math.Min(progress, 100) });
                        cell.Children.Add(Label($"{summary.Measured?.ToString("0.##") ?? "—"} / {summary.Guidance:0.##} {Unit(snapshot.Unit)}", 11));
                        Grid.SetColumn(cell, weekly ? 1 : 0);
                        budgets.Children.Add(cell);
                    }
                    content.Children.Add(budgets);
                    content.Children.Add(Label($"固定配分：日 {guidance.Daily:0.##} / 週 {guidance.Weekly:0.##} {Unit(guidance.Unit)}", 11));
                }
                else content.Children.Add(Label("日・週の目安：総枠または期間が未取得", 11));
            }
            if (failure is not null)
                content.Children.Add(new TextBlock { Text = $"取得状態: {failure.Kind} · {failure.Code}", Foreground = (Brush)FindResource("WarningBrush") });
            Cards.Children.Add(new Border
            {
                Background = (Brush)FindResource("SoftBrush"), BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Child = content
            });
        }
        Footer.Text = app.IsDemo ? "架空データのデモ · 接続・認証・保存は行いません"
            : $"{app.State.Polling.Interval.TotalMinutes:0}分ごとに更新 · 日付はJST / 週は月曜開始 · 記録はローカル";
        Error.Text = app.LastError ?? "";
    }

    private TextBlock Label(string text, double size = 12, bool bold = false) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = (Brush)FindResource(bold ? "TextBrush" : "MutedBrush"),
        Margin = new Thickness(0, 3, 0, 3)
    };
    internal static string Unit(UsageUnit unit) => unit switch
    { UsageUnit.PercentagePoints => "pt", UsageUnit.Credits => "クレジット", UsageUnit.Requests => "回", _ => "単位不明" };
    private static string CoverageLabel(Coverage coverage) => coverage switch
    { Coverage.Partial => "部分計測", Coverage.CompleteToObservation => "観測時点まで", _ => "未計測" };
}
