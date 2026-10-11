using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Core;
using AiUsageTray.Diagnostics;

namespace AiUsageTray.Desktop;

public sealed class TrayHost
{
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim refresh = new(0, 1);
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly UsageHttp http = new();
    private readonly SynchronizationContext context;
    private readonly NativeTray tray;
    private readonly string[] args;
    private readonly string pipeName = "ai-usage-tray-" + Guid.NewGuid().ToString("N");
    private readonly Dictionary<Provider, UsageFailure?> failures = [];
    private UsageState state = new();
    private DesktopSettings settings = new();
    private StateStore? store;
    private string profile = "";
    private string settingsPath = "";
    private string? lastError;
    private Process? panel;
    private Process? updateProcess;
    private volatile int panelPid;
    private volatile int updatePid;
    private volatile bool openSettings;
    private bool exiting;
    private Task? polling;
    private Task? server;

    public TrayHost(string[] args)
    {
        this.args = args;
        tray = new NativeTray(() => ShowPanel(false), RequestRefresh, () => ShowPanel(true),
            async () => await ExitAsync(), error => lastError = error,
            CheckUpdates, async () => await ToggleStartupAsync(), () => settings.StartAtLogon);
        SynchronizationContext.SetSynchronizationContext(new NativeContext(tray));
        context = SynchronizationContext.Current!;
        context.Post(async _ => await InitializeAsync(), null);
    }

    public void Run() => tray.Run();

    private async Task InitializeAsync()
    {
        try
        {
            profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiUsageTray");
            int index = Array.IndexOf(args, "--profile");
            if (index >= 0 && index + 1 < args.Length) profile = Path.GetFullPath(args[index + 1]);
            store = new StateStore(Path.Combine(profile, "state.json"));
            settingsPath = Path.Combine(profile, "settings.json");
            state = await store.LoadAsync(stopping.Token);
            settings = await DesktopSettings.LoadAsync(settingsPath, stopping.Token);
            if (!args.Contains("--profile"))
                settings = settings with { StartAtLogon = StartupRegistration.IsEnabled(Environment.ProcessPath!) };
            foreach (var provider in Enum.GetValues<Provider>())
                state.Bind(provider, settings.SelectionId(provider));
            tray.Show();
            Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
            server = ServeAsync();
            polling = PollAsync();
            if (!args.Contains("--background")) ShowPanel(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UsageException or Win32Exception)
        {
            NativeTray.ShowError($"起動できませんでした。既に実行中の場合はトレイを確認してください。\n{ex.GetType().Name}");
            await ExitAsync();
        }
    }

    private void ShowPanel(bool showSettings)
    {
        if (exiting || store is null) return;
        openSettings = showSettings;
        if (panel is { HasExited: false })
        {
            FocusPanel();
            return;
        }
        panel?.Dispose();
        var executable = Environment.ProcessPath ?? throw new UsageException(FailureKind.Cli, "実行パス不明");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "--panel", "--pipe", pipeName })
            info.ArgumentList.Add(argument);
        foreach (var name in new[] { "TYPESAFE_API_KEY", "GH_TOKEN", "GITHUB_TOKEN",
            "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "OPENAI_API_KEY", "ANTHROPIC_API_KEY" })
            info.Environment.Remove(name);
        try
        {
            panel = Process.Start(info) ?? throw new UsageException(FailureKind.Cli, "画面を起動できませんでした。");
            panelPid = panel.Id;
        }
        catch (Win32Exception)
        {
            tray.Notify("AI 利用状況", "画面を起動できませんでした。実行ファイルを確認してください。", error: true);
        }
    }

    private void FocusPanel()
    {
        var pid = panelPid;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner == pid && IsWindowVisible(handle))
            {
                ShowWindow(handle, 9);
                SetForegroundWindow(handle);
                return false;
            }
            return true;
        }, IntPtr.Zero);
    }

    private void RequestRefresh()
    {
        if (!exiting && refresh.CurrentCount == 0) refresh.Release();
    }

    private void CheckUpdates()
    {
        if (exiting || updateProcess is { HasExited: false }) return;
        if (args.Contains("--profile"))
        {
            NativeTray.ShowInformation("検証プロファイルではアプリのインストール更新を実行しません。\n更新確認のみ: " +
                UpdateFlow.ReleasePage);
            return;
        }
        updateProcess?.Dispose();
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var value in new[] { "--update-check", "--pipe", pipeName, "--host", Environment.ProcessId.ToString() })
            info.ArgumentList.Add(value);
        foreach (var name in new[] { "TYPESAFE_API_KEY", "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN",
            "GITHUB_ENTERPRISE_TOKEN", "OPENAI_API_KEY", "ANTHROPIC_API_KEY" })
            info.Environment.Remove(name);
        try
        {
            updateProcess = Process.Start(info);
            updatePid = updateProcess?.Id ?? 0;
        }
        catch (Win32Exception) { tray.Notify("AI 利用状況", "更新確認を起動できませんでした。", error: true); }
    }

    private async Task ToggleStartupAsync()
    {
        bool enable = !settings.StartAtLogon;
        if (!NativeTray.Confirm(enable ? "このアプリをログオン時にトレイで起動しますか？" :
            "このアプリのログオン時起動を解除しますか？")) return;
        await stateGate.WaitAsync(stopping.Token);
        try
        {
            await SaveSettingsAsync(new("save", settings with { StartAtLogon = enable },
                state.Rules, state.QuotaRules), stopping.Token);
        }
        catch (Exception ex) when (ex is UsageException or IOException or UnauthorizedAccessException)
        { tray.Notify("AI 利用状況", "起動設定を変更できませんでした: " + ex.GetType().Name, error: true); }
        finally { stateGate.Release(); }
    }

    private void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            context.Post(_ => RequestRefresh(), null);
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint caller) ||
                        (caller != panelPid && caller != updatePid))
                        throw new UsageException(FailureKind.Forbidden, "ipc_client_not_owned");
                    var request = await PipeProtocol.ReadAsync<DesktopRequest>(pipe, deadline.Token);
                    bool updateExit = caller == updatePid && request.Method == "update-exit";
                    if (caller == updatePid && !updateExit)
                        throw new UsageException(FailureKind.Forbidden, "ipc_update_method_not_allowed");
                    await stateGate.WaitAsync(deadline.Token);
                    DesktopResponse response;
                    try
                    {
                        switch (request.Method)
                        {
                            case "snapshot": break;
                            case "refresh": RequestRefresh(); break;
                            case "save":
                                if (caller != panelPid) throw new UsageException(FailureKind.Forbidden, "ipc_settings_owner_required");
                                await SaveSettingsAsync(request, deadline.Token);
                                break;
                            case "update-exit" when updateExit: break;
                            default: throw new UsageException(FailureKind.Forbidden, "ipc_method_not_allowed");
                        }
                        response = new(true, View: CreateView());
                    }
                    catch (UsageException ex) { response = new(false, ex.SafeCode); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    { response = new(false, "設定を保存できませんでした: " + ex.GetType().Name); }
                    finally { stateGate.Release(); }
                    await PipeProtocol.WriteAsync(pipe, response, deadline.Token);
                    if (updateExit && response.Success)
                        context.Post(async _ => await ExitAsync(forUpdate: true), null);
                }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                { lastError = "画面との通信がタイムアウトしました。"; }
                catch (Exception ex) when (ex is IOException or JsonException or UsageException)
                {
                    // No token or request contents are logged; the next UI connection can recover.
                    lastError = "画面との通信に失敗しました: " + ex.GetType().Name;
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (IOException)
        {
            lastError = "画面との通信を停止しました。アプリを再起動してください。";
            context.Post(_ => tray.Notify("AI 利用状況", lastError, error: true), null);
        }
    }

    private DesktopView CreateView()
    {
        var display = new UsageState
        {
            Rules = state.Rules, QuotaRules = state.QuotaRules,
            Polling = state.Polling, Latest = state.Latest.ToList()
        };
        var quotas = state.Latest.Select(s => new QuotaView(s.Key,
            Budgets.Summarize(state, s, false), Budgets.Summarize(state, s, true))).ToList();
        bool showSettings = openSettings;
        openSettings = false;
        return new(display, settings, new(failures), lastError, quotas, showSettings);
    }

    private async Task SaveSettingsAsync(DesktopRequest request, CancellationToken cancellationToken)
    {
        var updated = request.Settings ?? throw new UsageException(FailureKind.Schema, "接続設定がありません。");
        var defaults = request.Defaults ?? throw new UsageException(FailureKind.Schema, "通知設定がありません。");
        var rules = request.QuotaRules ?? throw new UsageException(FailureKind.Schema, "枠設定がありません。");
        updated.Validate(); defaults.Validate();
        foreach (var rule in rules) rule.Rules.Validate();
        var previous = settings;
        if (args.Contains("--profile") && updated.StartAtLogon != previous.StartAtLogon)
            throw new UsageException(FailureKind.Forbidden, "検証プロファイルからログオン設定は変更しません。");
        await updated.SaveAsync(settingsPath, cancellationToken);
        if (updated.StartAtLogon != previous.StartAtLogon) DesktopSettings.SetStartAtLogon(updated.StartAtLogon);
        state.Rules = defaults; state.QuotaRules = rules;
        foreach (var provider in Enum.GetValues<Provider>())
            state.Bind(provider, updated.SelectionId(provider));
        await store!.SaveAsync(state, cancellationToken);
        settings = updated;
        failures.Clear(); lastError = null;
        RequestRefresh();
    }

    private async Task PollAsync()
    {
        var counts = Enum.GetValues<Provider>().ToDictionary(p => p, _ => 0);
        var next = Enum.GetValues<Provider>().ToDictionary(p => p, _ => DateTimeOffset.MinValue);
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                foreach (var provider in Enum.GetValues<Provider>())
                {
                    if (DateTimeOffset.UtcNow < next[provider]) continue;
                    var selected = settings;
                    var fetcher = selected.CreateProvider(provider, http);
                    if (fetcher is null) continue;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    deadline.CancelAfter(state.Polling.Timeout);
                    FetchResult result;
                    try { result = await fetcher.FetchAsync(deadline.Token); }
                    catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                    { result = FetchResult.Failed(new(FailureKind.Timeout, "取得タイムアウト", DateTimeOffset.UtcNow)); }
                    await stateGate.WaitAsync(stopping.Token);
                    try
                    {
                        if (selected != settings) continue;
                        counts[provider] = result.Succeeded ? 0 : counts[provider] + 1;
                        failures[provider] = result.Failures.LastOrDefault();
                        var retry = result.Failures.Select(f => f.RetryAfter ?? TimeSpan.Zero).DefaultIfEmpty().Max();
                        next[provider] = DateTimeOffset.UtcNow + Polling.NextDelay(state.Polling, counts[provider], retry);
                        var notices = UsageEngine.Apply(state, result, DateTimeOffset.UtcNow);
                        await store!.SaveAsync(state, stopping.Token);
                        if (notices.Count > 0)
                            tray.Notify(provider.ToString(), string.Join("\n", notices.Select(NoticeText)));
                    }
                    finally { stateGate.Release(); }
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                var delay = Task.Delay(state.Polling.Interval, wait.Token);
                var requested = refresh.WaitAsync(wait.Token);
                if (await Task.WhenAny(delay, requested) == requested)
                {
                    await stateGate.WaitAsync(stopping.Token);
                    try
                    {
                        foreach (var provider in Enum.GetValues<Provider>())
                            if (failures.GetValueOrDefault(provider)?.Kind != FailureKind.RateLimited)
                                next[provider] = DateTimeOffset.MinValue;
                    }
                    finally { stateGate.Release(); }
                }
                wait.Cancel();
                try { await Task.WhenAll(delay, requested); }
                catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UsageException)
        {
            lastError = "記録・取得を停止しました: " + ex.GetType().Name;
            tray.Notify("AI 利用状況", lastError, error: true);
        }
    }

    private static string NoticeText(NotificationCandidate n) => n.Reason switch
    {
        NotificationReason.DailyGuidance => $"{n.Key.Bucket}: 今日の目安を超過（観測分）",
        NotificationReason.WeeklyGuidance => $"{n.Key.Bucket}: 今週の目安を超過（観測分）",
        NotificationReason.Reset => $"{n.Key.Bucket}: 新しいクォータ期間を確認",
        NotificationReason.Threshold => $"{n.Key.Bucket}: しきい値 {string.Join(" / ", n.CrossedLevels)}% に到達",
        _ => $"{n.Key.Bucket}: 使用率 {n.CrossedLevels.LastOrDefault():0.##}% の刻みに到達"
    };

    private async Task ExitAsync(bool forUpdate = false)
    {
        if (exiting) return;
        exiting = true;
        if (panel is { HasExited: false })
        {
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint owner);
                if (owner == panelPid && IsWindowVisible(handle)) PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await panel.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { await ReadOnlyProcess.CloseAsync(panel); }
        }
        stopping.Cancel();
        if (!forUpdate && updateProcess is { HasExited: false })
            await ReadOnlyProcess.CloseAsync(updateProcess);
        if (polling is not null) await polling;
        if (server is not null) await server;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
        panel?.Dispose(); updateProcess?.Dispose(); tray.Dispose(); store?.Dispose(); http.Dispose();
        tray.Stop();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
