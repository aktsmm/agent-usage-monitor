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
    private HistoryWindow? historyWindow;
    private List<QuotaView> quotas = [];
    private bool updating;
    private bool noActivate;
    public UsageState State { get; private set; } = new();
    public DesktopSettings Settings { get; private set; } = new();
    public Dictionary<Provider, UsageFailure?> LastFailures { get; private set; } = [];
    public string? LastError { get; private set; }
    public bool IsDemo { get; private set; }
    public bool Discovering { get; private set; }
    public UpdateStatus? Update { get; private set; }
    public HistoryStorageStatus? HistoryStatus { get; private set; }
    public UiLocalizer Localizer { get; private set; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.Contains("--demo"))
            {
                IsDemo = true;
                noActivate = e.Args.Contains("--no-activate");
                var demo = DemoView.Create(e.Args.Contains("--light"));
                int language = Array.IndexOf(e.Args, "--ui-language");
                if (language >= 0 && language + 1 < e.Args.Length)
                    demo = demo with { Settings = demo.Settings with { Language = e.Args[language + 1] switch
                    {
                        "ja" => DisplayLanguage.Japanese, "en" => DisplayLanguage.English,
                        _ => throw new UsageException(FailureKind.Schema, "helper_language_invalid")
                    } } };
                Accept(demo);
                if (e.Args.Contains("--demo-settings")) OpenSettings(showAppearance: true);
                else ShowOverview();
                return;
            }
            int index = Array.IndexOf(e.Args, "--pipe");
            if (index < 0 || index + 1 >= e.Args.Length)
                throw new UsageException(FailureKind.Forbidden, "panel_required");
            pipeName = e.Args[index + 1];
            var view = await PipeProtocol.SendAsync(pipeName, new("snapshot"), stopping.Token);
            Accept(view);
            if (view.OpenHistory) OpenHistory();
            else if (view.OpenSettings) OpenSettings(); else ShowOverview();
            timer.Tick += async (_, _) => await UpdateAsync();
            timer.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UsageException or OperationCanceledException)
        {
            MessageBox.Show(Localizer.Text(UiText.CommunicationFailed) + "\n" + ex.GetType().Name, Localizer.Text(UiText.AgentUsage));
            Shutdown(1);
        }
    }

    private void Accept(DesktopView view)
    {
        State = view.State; Settings = view.Settings; LastFailures = view.Failures;
        LastError = view.Error; quotas = view.Quotas;
        Discovering = view.Discovering; Update = view.Update;
        HistoryStatus = view.HistoryStatus;
        var language = new UiLocalizer(Settings.Language);
        bool changedLanguage = language.LanguageTag != Localizer.LanguageTag;
        Localizer = language;
        foreach (var key in Enum.GetValues<UiText>())
            Resources[key.ToString()] = Localizer.Text(key);
        ApplyTheme();
        overview?.Render();
        settingsWindow?.RenderStatus();
        if (changedLanguage) historyWindow?.SetLanguage(Localizer);
        if (HistoryStatus is { } status) historyWindow?.SetStorageStatus(status);
    }

    private async Task UpdateAsync()
    {
        if (updating || stopping.IsCancellationRequested || historyWindow is { IsBusy: true }) return;
        updating = true;
        try
        {
            var view = await PipeProtocol.SendAsync(pipeName, new("snapshot"), stopping.Token);
            Accept(view);
            if (view.OpenSettings) OpenSettings();
            if (view.OpenHistory) OpenHistory();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or JsonException or UsageException or OperationCanceledException)
        {
            LastError = "ipc_request_failed";
            overview?.Render();
            timer.Stop();
        }
        finally { updating = false; }
    }

    public ConsumptionSummary Consumption(QuotaSnapshot snapshot, bool weekly)
    {
        var view = quotas.FirstOrDefault(q => q.Key == snapshot.Key)
            ?? throw new UsageException(FailureKind.Schema, "consumption_view_missing");
        return weekly ? view.Weekly : view.Daily;
    }

    private void ShowOverview()
    {
        overview ??= new MainWindow(this);
        overview.ShowActivated = !noActivate;
        overview.Show();
        if (!noActivate) overview.Activate();
        overview.Render();
    }

    public void OverviewClosed(MainWindow window)
    {
        if (overview == window) overview = null;
        if (MainWindow == window) MainWindow = null;
        if (settingsWindow is null && historyWindow is null) Shutdown();
    }

    public void OpenSettings(bool showAppearance = false)
    {
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(this, showAppearance);
        settingsWindow.ShowActivated = !noActivate;
        settingsWindow.Closed += (_, _) =>
        {
            if (MainWindow == settingsWindow) MainWindow = null;
            settingsWindow = null;
            if (overview is null && historyWindow is null) Shutdown();
        };
        settingsWindow.Show();
    }

    public void OpenHistory()
    {
        if (historyWindow is not null) { historyWindow.Activate(); return; }
        historyWindow = new HistoryWindow(async (request, cancellationToken) =>
        {
            if (IsDemo) throw new UsageException(FailureKind.Forbidden, "demo_history_not_saved");
            return await PipeProtocol.HistoryAsync(pipeName, request, cancellationToken);
        }, Localizer);
        historyWindow.Closed += (_, _) =>
        {
            if (MainWindow == historyWindow) MainWindow = null;
            historyWindow = null;
            if (overview is null && settingsWindow is null) Shutdown();
        };
        historyWindow.Show();
    }

    public async void RequestRefresh()
    {
        if (IsDemo) return;
        try { Accept(await PipeProtocol.SendAsync(pipeName, new("refresh"), stopping.Token)); }
        catch (Exception ex) when (ex is IOException or JsonException or UsageException or OperationCanceledException)
        {
            LastError = "ipc_refresh_failed";
            overview?.Render();
        }
    }

    public async Task SaveSettingsAsync(DesktopSettings settings, NotificationRules defaults, List<QuotaRule> rules,
        bool usageStripPreferenceChanged = false)
    {
        if (IsDemo) throw new UsageException(FailureKind.Forbidden, "demo_read_only");
        Accept(await PipeProtocol.SendAsync(pipeName, new("save", settings, defaults, rules,
            UsageStripPreferenceChanged: usageStripPreferenceChanged), stopping.Token));
    }

    public async Task ConnectionActionAsync(string method, Provider? provider = null)
    {
        if (IsDemo) throw new UsageException(FailureKind.Forbidden, "demo_read_only");
        Accept(await PipeProtocol.SendAsync(pipeName, new(method, Provider: provider), stopping.Token));
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
