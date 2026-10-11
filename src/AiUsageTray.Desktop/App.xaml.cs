using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public partial class App : Application
{
    private readonly CancellationTokenSource stopping = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string pipeName = "";
    private MainWindow? overview;
    private SettingsWindow? settingsWindow;
    private List<QuotaView> quotas = [];
    private bool updating;
    public UsageState State { get; private set; } = new();
    public DesktopSettings Settings { get; private set; } = new();
    public Dictionary<Provider, UsageFailure?> LastFailures { get; private set; } = [];
    public string? LastError { get; private set; }
    public bool IsDemo { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.Contains("--demo"))
            {
                IsDemo = true;
                Accept(DemoView.Create(e.Args.Contains("--light")));
                ShowOverview();
                return;
            }
            int index = Array.IndexOf(e.Args, "--pipe");
            if (index < 0 || index + 1 >= e.Args.Length)
                throw new UsageException(FailureKind.Forbidden, "画面はトレイから起動してください。");
            pipeName = e.Args[index + 1];
            var view = await PipeProtocol.SendAsync(pipeName, new("snapshot"), stopping.Token);
            Accept(view);
            if (view.OpenSettings) OpenSettings(); else ShowOverview();
            timer.Tick += async (_, _) => await UpdateAsync();
            timer.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UsageException or OperationCanceledException)
        {
            MessageBox.Show("常駐本体に接続できませんでした。トレイから開き直してください。\n" + ex.GetType().Name, "AI 利用状況");
            Shutdown(1);
        }
    }

    private void Accept(DesktopView view)
    {
        State = view.State; Settings = view.Settings; LastFailures = view.Failures;
        LastError = view.Error; quotas = view.Quotas;
        ApplyTheme();
        overview?.Render();
    }

    private async Task UpdateAsync()
    {
        if (updating || stopping.IsCancellationRequested) return;
        updating = true;
        try
        {
            var view = await PipeProtocol.SendAsync(pipeName, new("snapshot"), stopping.Token);
            Accept(view);
            if (view.OpenSettings) OpenSettings();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or JsonException or UsageException or OperationCanceledException)
        {
            LastError = "常駐本体との通信に失敗しました。トレイから開き直してください。";
            overview?.Render();
            timer.Stop();
        }
        finally { updating = false; }
    }

    public ConsumptionSummary Consumption(QuotaSnapshot snapshot, bool weekly)
    {
        var view = quotas.FirstOrDefault(q => q.Key == snapshot.Key)
            ?? throw new UsageException(FailureKind.Schema, "表示用の観測差分がありません。");
        return weekly ? view.Weekly : view.Daily;
    }

    private void ShowOverview()
    {
        overview ??= new MainWindow(this);
        overview.Show(); overview.Activate(); overview.Render();
    }

    public void OverviewClosed(MainWindow window)
    {
        if (overview == window) overview = null;
        if (MainWindow == window) MainWindow = null;
        if (settingsWindow is null) Shutdown();
    }

    public void OpenSettings()
    {
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(this);
        settingsWindow.Closed += (_, _) =>
        {
            if (MainWindow == settingsWindow) MainWindow = null;
            settingsWindow = null;
            if (overview is null) Shutdown();
        };
        settingsWindow.Show();
    }

    public async void RequestRefresh()
    {
        if (IsDemo) return;
        try { Accept(await PipeProtocol.SendAsync(pipeName, new("refresh"), stopping.Token)); }
        catch (Exception ex) when (ex is IOException or JsonException or UsageException or OperationCanceledException)
        {
            LastError = "更新を要求できませんでした。トレイから開き直してください。";
            overview?.Render();
        }
    }

    public async Task SaveSettingsAsync(DesktopSettings settings, NotificationRules defaults, List<QuotaRule> rules)
    {
        if (IsDemo) throw new UsageException(FailureKind.Forbidden, "デモは保存・接続しません。");
        Accept(await PipeProtocol.SendAsync(pipeName, new("save", settings, defaults, rules), stopping.Token));
    }

    public void ApplyTheme()
    {
        bool dark = Settings.Theme == "Dark";
        foreach (var (key, light, night) in new[]
        {
            ("BackgroundBrush", "#FFFFFF", "#292929"), ("SoftBrush", "#F5F5F5", "#2E2E2E"),
            ("TextBrush", "#242424", "#DEDEDE"), ("MutedBrush", "#5C5C5C", "#B0B0B0"),
            ("BorderBrush", "#DEDEDE", "#474747"), ("AccentBrush", "#B11F4B", "#FD8EA1"),
            ("WarningBrush", "#B45309", "#FBBF24")
        })
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        timer.Stop();
        stopping.Cancel();
        base.OnExit(e);
    }
}
