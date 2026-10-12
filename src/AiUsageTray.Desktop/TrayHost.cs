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
using Velopack.Locators;
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
    private readonly HashSet<Provider> verifiedSelections = [];
    private UsageState state = new();
    private DesktopSettings settings = new();
    private StateStore? store;
    private HistoryService? history;
    private HistoryCommands? historyCommands;
    private NativeUsageStrip? usageStrip;
    private string profile = "";
    private string settingsPath = "";
    private string? lastError;
    private Process? panel;
    private Process? updateProcess;
    private volatile int panelPid;
    private volatile int updatePid;
    private volatile bool openSettings;
    private volatile bool openHistory;
    private bool exiting;
    private Task? polling;
    private Task? server;
    private Task? discoveryRun;
    private CancellationTokenSource? discoveryCancellation;
    private UpdateStatus update = new(UpdateStage.Idle, "update_idle");
    private bool updateAutomatic;
    private bool updateReserved;
    private bool resumeDeferred;
    private bool initialized;
    private bool shutdownComplete;
    private HostShutdownReason shutdownReason;
    private HostDiagnostics? diagnostics;
    private UiLocalizer Locale => new(settings.Language);

    public TrayHost(string[] args)
    {
        this.args = args;
        tray = new NativeTray(() => ShowPanel(false), RequestRefresh, () => ShowPanel(true),
            async () => await ExitFromTrayAsync(), error => lastError = error,
            CheckUpdates, async () => await ToggleStartupAsync(), () => settings.StartAtLogon,
            async () => await ToggleUsageStripAsync(), () => settings.UsageStripEnabled,
            () => { openHistory = true; ShowPanel(false); }, () => new(settings.Language));
        SynchronizationContext.SetSynchronizationContext(new NativeContext(tray));
        context = SynchronizationContext.Current!;
        context.Post(async _ => await InitializeAsync(), null);
    }

    public void Run()
    {
        int code = tray.Run();
        diagnostics?.Write(HostEvent.LoopReturned, shutdownReason, exitCode: code);
        if (!shutdownComplete)
            throw new InvalidOperationException("host_message_loop_ended_without_shutdown");
        Environment.ExitCode = code;
        if (code == 0) diagnostics?.Write(HostEvent.CleanExit, shutdownReason, exitCode: code);
        GC.KeepAlive(this);
    }

    public void RecordFatal(Exception exception)
    {
        Environment.ExitCode = 1;
        try { diagnostics?.Write(HostEvent.Fatal, shutdownReason, exception, 1); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("host_diagnostics_write_failed:" + ex.GetType().Name); }
    }

    public void ReleaseLease() => store?.Dispose();
    public void ShowFatal(Exception exception) =>
        NativeTray.ShowError(Locale.Format(UiText.HostStopped, exception.GetType().Name), Locale);
    private Task ExitFromTrayAsync() => ExitAsync(reason: tray.CloseReason);

    private Task Observe(Task task, HostEvent stage)
    {
        _ = task.ContinueWith(faulted =>
        {
            var exception = faulted.Exception!.GetBaseException();
            context.Post(_ =>
            {
                diagnostics?.Write(stage, shutdownReason, exception, 1);
                tray.Fail(exception);
            }, null);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return task;
    }

    private async Task InitializeAsync()
    {
        try
        {
            profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiUsageTray");
            int index = Array.IndexOf(args, "--profile");
            if (index >= 0 && index + 1 < args.Length) profile = Path.GetFullPath(args[index + 1]);
            store = new StateStore(Path.Combine(profile, "state.json"));
            diagnostics = new(profile, UpdateFlow.Version);
            tray.Lifecycle = kind => diagnostics.Write(kind, shutdownReason);
            tray.StartHeartbeat();
            settingsPath = Path.Combine(profile, "settings.json");
            state = await store.LoadAsync(stopping.Token);
            history = await HistoryService.LoadAsync(profile, stopping.Token);
            historyCommands = new(history);
            await history.RecordAsync(DailyHistory.Import(state), state.Polling.MaximumAge, stopping.Token);
            settings = await DesktopSettings.LoadAsync(settingsPath, stopping.Token);
            settings = settings.PinExisting(state);
            if (!args.Contains("--profile"))
                settings = settings with { StartAtLogon = StartupRegistration.IsEnabled(Environment.ProcessPath!) };
            foreach (var provider in Enum.GetValues<Provider>())
                state.Bind(provider, settings.SelectionId(provider));
            await settings.SaveAsync(settingsPath, stopping.Token);
            usageStrip = new NativeUsageStrip(tray.Handle, () => ShowPanel(false),
                (x, y) => tray.ShowMenu(x, y), async position => await SaveStripPositionAsync(position),
                error =>
                {
                    lastError = error;
                    diagnostics.Write(HostEvent.StripFailed);
                    tray.Notify(Locale.Text(UiText.AgentUsage), Locale.Error(error), error: true);
                }, tray.Fail);
            RefreshUsageStrip();
            tray.Show();
            Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
            server = Observe(ServeAsync(), HostEvent.IpcFault);
            polling = Observe(PollAsync(), HostEvent.PollingFault);
            initialized = true;
            diagnostics.Write(HostEvent.Ready);
            if (!args.Contains("--background")) ShowPanel(false);
            StartDiscovery(null, initial: true);
            if (!args.Contains("--profile") && settings.AutoUpdateEnabled)
                CheckUpdates(automatic: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UsageException or Win32Exception)
        {
            RecordFatal(ex);
            NativeTray.ShowError(Locale.Format(UiText.InitializationFailed, ex.GetType().Name), Locale);
            await ExitAsync(reason: HostShutdownReason.InitializationFailed);
        }
    }

    private void ShowPanel(bool showSettings)
    {
        if (exiting || updateReserved || store is null) return;
        openSettings = showSettings;
        if (panel is { HasExited: false })
        {
            FocusPanel();
            return;
        }
        panel?.Dispose();
        var executable = Environment.ProcessPath ?? throw new UsageException(FailureKind.Cli, "app_executable_missing");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "--panel", "--pipe", pipeName })
            info.ArgumentList.Add(argument);
        foreach (var name in new[] { "TYPESAFE_API_KEY", "GH_TOKEN", "GITHUB_TOKEN",
            "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "OPENAI_API_KEY", "ANTHROPIC_API_KEY",
            "COPILOT_GITHUB_TOKEN", "CODEX_ACCESS_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_AUTH_TOKEN",
            "COPILOT_PROVIDER_API_KEY", "AZURE_OPENAI_API_KEY" })
            info.Environment.Remove(name);
        try
        {
            panel = Process.Start(info) ?? throw new UsageException(FailureKind.Cli, "panel_start_failed");
            panelPid = panel.Id;
            panel.EnableRaisingEvents = true;
            panel.Exited += (_, _) =>
            {
                if (stopping.IsCancellationRequested) return;
                context.Post(_ =>
                {
                    diagnostics?.Write(HostEvent.PanelExited);
                    ResumeDeferredUpdate();
                }, null);
            };
        }
        catch (Win32Exception)
        {
            tray.Notify(Locale.Text(UiText.AgentUsage), Locale.Text(UiText.PanelStartFailed), error: true);
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

    private void CheckUpdates() => CheckUpdates(automatic: false);

    private void CheckUpdates(bool automatic)
    {
        if (exiting || updateProcess is { HasExited: false }) return;
        if (automatic && (!settings.AutoUpdateEnabled || update.RetryAt > DateTimeOffset.UtcNow)) return;
        if (args.Contains("--profile"))
        {
            if (automatic) return;
            NativeTray.ShowInformation(Locale.Format(UiText.UpdateProfileBlocked, UpdateFlow.ReleasePage), Locale);
            return;
        }
        if (automatic && VelopackLocator.Current.CurrentlyInstalledVersion is null) return;
        updateProcess?.Dispose();
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var value in new[] { automatic ? "--update-auto" : "--update-check", "--pipe", pipeName, "--host", Environment.ProcessId.ToString() })
            info.ArgumentList.Add(value);
        info.ArgumentList.Add("--ui-language");
        info.ArgumentList.Add(new UiLocalizer(settings.Language).LanguageTag);
        foreach (var name in new[] { "TYPESAFE_API_KEY", "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN",
            "GITHUB_ENTERPRISE_TOKEN", "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "COPILOT_GITHUB_TOKEN",
            "CODEX_ACCESS_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_AUTH_TOKEN",
            "COPILOT_PROVIDER_API_KEY", "AZURE_OPENAI_API_KEY" })
            info.Environment.Remove(name);
        try
        {
            updateProcess = Process.Start(info);
            updatePid = updateProcess?.Id ?? 0;
            updateAutomatic = automatic;
            update = new(UpdateStage.Checking, "update_checking");
            if (updateProcess is not null)
            {
                updateProcess.EnableRaisingEvents = true;
                updateProcess.Exited += (_, _) =>
                {
                    if (stopping.IsCancellationRequested) return;
                    context.Post(_ =>
                    {
                        diagnostics?.Write(HostEvent.UpdateHelperExited);
                        if (!exiting && update.Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Ready)
                            update = new(UpdateStage.Failed, "update_helper_ended", ErrorCode: "update_helper_ended");
                        ResumeDeferredUpdate();
                    }, null);
                };
            }
        }
        catch (Win32Exception)
        {
            update = new(UpdateStage.Failed, "update_helper_start_failed", ErrorCode: "update_helper_start_failed");
            if (!automatic) tray.Notify(Locale.Text(UiText.AgentUsage), update.Display(Locale), error: true);
        }
    }

    private void ResumeDeferredUpdate()
    {
        if (exiting || update.Stage != UpdateStage.Deferred || panel is { HasExited: false } ||
            discoveryRun is { IsCompleted: false } || updateProcess is { HasExited: false }) return;
        if (!resumeDeferred || updateAutomatic && !settings.AutoUpdateEnabled) return;
        resumeDeferred = false;
        // Recheck the same stable feed and pending package; no helper stays resident while a UI is open.
        CheckUpdates(automatic: true);
    }

    private void StartDiscovery(Provider? provider, bool initial = false)
    {
        if (discoveryRun is { IsCompleted: false } || exiting) return;
        discoveryCancellation?.Dispose();
        discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        discoveryRun = Observe(DiscoverAsync(provider, initial, discoveryCancellation.Token), HostEvent.DiscoveryFault);
    }

    private async Task DiscoverAsync(Provider? requested, bool initial, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var provider in requested is { } only ? new[] { only } : Enum.GetValues<Provider>())
            {
                DesktopSettings selection;
                await stateGate.WaitAsync(cancellationToken);
                try
                {
                    if (initial && settings.Mode(provider) != ConnectionMode.Pending) continue;
                    if (initial)
                    {
                        settings = settings.WithMode(provider, ConnectionMode.Attempted);
                        await settings.SaveAsync(settingsPath, cancellationToken);
                    }
                    selection = settings;
                }
                finally { stateGate.Release(); }
                var result = await new ConnectionDiscovery(http).ProbeAsync(provider, selection.CodexHome, cancellationToken);
                await stateGate.WaitAsync(stopping.Token);
                try
                {
                    var updated = settings.CompleteDiscovery(selection, result, initial);
                    await updated.SaveAsync(settingsPath, stopping.Token);
                    settings = updated;
                    state.Bind(provider, updated.SelectionId(provider));
                    if (initial && result.Status == DiscoveryStatus.Ready &&
                        updated.Mode(provider) == ConnectionMode.Automatic &&
                        updated.PinnedAccount(provider) == result.AccountKey && result.Usage is { } usage)
                    {
                        int before = state.Deltas.Count;
                        UsageEngine.Apply(state, usage, DateTimeOffset.UtcNow);
                        await RecordHistoryAsync(before, stopping.Token);
                        verifiedSelections.Add(provider);
                    }
                    await store!.SaveAsync(state, stopping.Token);
                    RefreshUsageStrip();
                    RequestRefresh();
                }
                finally { stateGate.Release(); }
                if (cancellationToken.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { lastError = "discovery_cancelled"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UsageException)
        {
            lastError = "discovery_save_failed:" + ex.GetType().Name;
            diagnostics?.Write(HostEvent.DiscoveryFailed, exception: ex);
        }
        finally { context.Post(_ => ResumeDeferredUpdate(), null); }
    }

    private async Task ToggleStartupAsync()
    {
        bool enable = !settings.StartAtLogon;
        if (!NativeTray.Confirm(Locale.Text(enable ? UiText.StartupEnableConfirm : UiText.StartupDisableConfirm), Locale)) return;
        await stateGate.WaitAsync(stopping.Token);
        try
        {
            await SaveSettingsAsync(new("save", settings with { StartAtLogon = enable },
                state.Rules, state.QuotaRules), stopping.Token);
        }
        catch (Exception ex) when (ex is UsageException or IOException or UnauthorizedAccessException)
        { tray.Notify(Locale.Text(UiText.AgentUsage), Locale.Format(UiText.SaveFailed, ex.GetType().Name), error: true); }
        finally { stateGate.Release(); }
    }

    private async Task ToggleUsageStripAsync()
    {
        if (exiting) return;
        await stateGate.WaitAsync();
        try
        {
            var updated = settings with { UsageStripEnabled = !settings.UsageStripEnabled };
            await updated.SaveAsync(settingsPath, stopping.Token);
            settings = updated;
            RefreshUsageStrip();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UsageException)
        {
            lastError = "usage_strip_settings_failed:" + ex.GetType().Name;
            tray.Notify(Locale.Text(UiText.AgentUsage), Locale.Error(lastError), error: true);
        }
        finally { stateGate.Release(); }
    }

    private async Task SaveStripPositionAsync(UsageStripPosition position)
    {
        if (exiting) return;
        await stateGate.WaitAsync();
        try
        {
            var updated = settings with { UsageStripPosition = position };
            await updated.SaveAsync(settingsPath, stopping.Token);
            settings = updated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UsageException)
        { lastError = "usage_strip_position_failed:" + ex.GetType().Name; }
        finally { stateGate.Release(); }
    }

    private void RefreshUsageStrip()
    {
        var selected = settings;
        var snapshots = state.Latest.Where(s => verifiedSelections.Contains(s.Key.Account.Provider)).ToArray();
        var age = state.Polling.MaximumAge;
        var failed = failures.Where(f => f.Value is not null).Select(f => f.Key).ToArray();
        context.Post(_ => usageStrip?.Update(selected, snapshots, age, failed), null);
    }

    private void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (stopping.IsCancellationRequested) return;
        context.Post(_ =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) diagnostics?.Write(HostEvent.PowerSuspend);
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) diagnostics?.Write(HostEvent.PowerResume);
        }, null);
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
                    if (request.Method == "history" && caller == panelPid)
                        deadline.CancelAfter(TimeSpan.FromMinutes(5));
                    bool updateExit = caller == updatePid && request.Method == "update-exit";
                    if (caller == updatePid && request.Method is not ("update-exit" or "update-status" or "update-permission"))
                        throw new UsageException(FailureKind.Forbidden, "ipc_update_method_not_allowed");
                    await stateGate.WaitAsync(deadline.Token);
                    DesktopResponse response;
                    HistoryResponse? historyResponse = null;
                    try
                    {
                        switch (request.Method)
                        {
                            case "snapshot": break;
                            case "history":
                                if (caller != panelPid || request.History is null || historyCommands is null)
                                    throw new UsageException(FailureKind.Forbidden, "ipc_history_owner_required");
                                historyResponse = await historyCommands.ExecuteAsync(request.History, deadline.Token);
                                if (historyResponse.Success && request.History.Action == HistoryAction.Archive &&
                                    request.History.Month is { } archived)
                                {
                                    var verified = await history!.Store.VerifyArchiveAsync(archived, deadline.Token);
                                    HistoryRetention.Prune(state, DateTimeOffset.UtcNow, verified);
                                    await store!.SaveAsync(state, deadline.Token);
                                }
                                break;
                            case "refresh": RequestRefresh(); break;
                            case "detect":
                                if (caller != panelPid || request.Provider is null || !Enum.IsDefined(request.Provider.Value))
                                    throw new UsageException(FailureKind.Forbidden, "ipc_discovery_owner_required");
                                context.Post(_ => StartDiscovery(request.Provider), null);
                                break;
                            case "detect-cancel":
                                if (caller != panelPid) throw new UsageException(FailureKind.Forbidden, "ipc_discovery_owner_required");
                                discoveryCancellation?.Cancel();
                                break;
                            case "update-check":
                                if (caller != panelPid) throw new UsageException(FailureKind.Forbidden, "ipc_settings_owner_required");
                                context.Post(_ => CheckUpdates(), null);
                                break;
                            case "update-status" when caller == updatePid:
                                update = request.Update ?? throw new UsageException(FailureKind.Schema, "ipc_update_status_missing");
                                if (!Enum.IsDefined(update.Stage)) throw new UsageException(FailureKind.Schema, "ipc_update_status_invalid");
                                break;
                            case "update-permission" when caller == updatePid:
                                if (!CanApplyUpdate())
                                {
                                    update = update with { Stage = UpdateStage.Deferred, Message = "update_deferred" };
                                    resumeDeferred = !updateAutomatic || settings.AutoUpdateEnabled;
                                }
                                break;
                            case "save":
                                if (caller != panelPid) throw new UsageException(FailureKind.Forbidden, "ipc_settings_owner_required");
                                await SaveSettingsAsync(request, deadline.Token);
                                break;
                            case "update-exit" when updateExit:
                                if (!CanApplyUpdate()) throw new UsageException(FailureKind.Forbidden, "update_deferred");
                                updateReserved = true;
                                break;
                            default: throw new UsageException(FailureKind.Forbidden, "ipc_method_not_allowed");
                        }
                        response = new(true, View: CreateView(), History: historyResponse);
                    }
                    catch (UsageException ex) { response = new(false, ex.SafeCode); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    { response = new(false, "settings_save_failed:" + ex.GetType().Name); }
                    finally { stateGate.Release(); }
                    await PipeProtocol.WriteAsync(pipe, response, deadline.Token);
                    if (updateExit && response.Success)
                        context.Post(async _ => await ExitAsync(forUpdate: true), null);
                }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                { lastError = "ipc_timeout"; }
                catch (Exception ex) when (ex is IOException or JsonException or UsageException)
                {
                    // No token or request contents are logged; the next UI connection can recover.
                    lastError = "ipc_failed:" + ex.GetType().Name;
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (IOException)
        {
            lastError = "ipc_stopped";
            diagnostics?.Write(HostEvent.IpcStopped);
            context.Post(_ => tray.Notify(Locale.Text(UiText.AgentUsage), Locale.Error(lastError), error: true), null);
        }
    }

    private DesktopView CreateView()
    {
        var display = new UsageState
        {
            Rules = state.Rules, QuotaRules = state.QuotaRules,
            Polling = state.Polling, Latest = state.Latest.Where(s => verifiedSelections.Contains(s.Key.Account.Provider)).ToList()
        };
        var quotas = display.Latest.Select(s => new QuotaView(s.Key,
            Budgets.Summarize(state, s, false), Budgets.Summarize(state, s, true))).ToList();
        bool showSettings = openSettings;
        openSettings = false;
        bool showHistory = openHistory;
        openHistory = false;
        return new(display, settings, new(failures), lastError, quotas, showSettings,
            discoveryRun is { IsCompleted: false }, update, history?.Status, showHistory);
    }

    private bool CanApplyUpdate() => UpdatePolicy.CanApply(settings.AutoUpdateEnabled, updateAutomatic,
        panel is { HasExited: false }, discoveryRun is { IsCompleted: false },
        VelopackLocator.Current.CurrentlyInstalledVersion is not null, VelopackLocator.Current.IsPortable);

    private async Task SaveSettingsAsync(DesktopRequest request, CancellationToken cancellationToken)
    {
        var updated = request.Settings ?? throw new UsageException(FailureKind.Schema, "settings_connection_missing");
        updated = updated with { ConnectionResults = settings.ConnectionResults, UsageStripPosition = settings.UsageStripPosition,
            UsageStripEnabled = request.UsageStripPreferenceChanged == false ? settings.UsageStripEnabled : updated.UsageStripEnabled };
        var defaults = request.Defaults ?? throw new UsageException(FailureKind.Schema, "settings_notification_missing");
        var rules = request.QuotaRules ?? throw new UsageException(FailureKind.Schema, "settings_quota_missing");
        updated.Validate(); defaults.Validate();
        foreach (var rule in rules) rule.Rules.Validate();
        var previous = settings;
        if (args.Contains("--profile") && updated.StartAtLogon != previous.StartAtLogon)
            throw new UsageException(FailureKind.Forbidden, "verification_startup_blocked");
        await updated.SaveAsync(settingsPath, cancellationToken);
        if (updated.StartAtLogon != previous.StartAtLogon) DesktopSettings.SetStartAtLogon(updated.StartAtLogon);
        state.Rules = defaults; state.QuotaRules = rules;
        foreach (var provider in Enum.GetValues<Provider>())
        {
            if (previous.SelectionId(provider) != updated.SelectionId(provider))
                verifiedSelections.Remove(provider);
            state.Bind(provider, updated.SelectionId(provider));
        }
        await store!.SaveAsync(state, cancellationToken);
        settings = updated;
        RefreshUsageStrip();
        if (!settings.AutoUpdateEnabled && updateAutomatic)
            update = update with { Stage = UpdateStage.Disabled, Message = "update_disabled" };
        failures.Clear(); lastError = null;
        RequestRefresh();
        context.Post(_ => StartDiscovery(null, initial: true), null);
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
                    { result = FetchResult.Failed(new(FailureKind.Timeout, "usage_fetch_timeout", DateTimeOffset.UtcNow)); }
                    await stateGate.WaitAsync(stopping.Token);
                    try
                    {
                        if (selected != settings) continue;
                        counts[provider] = result.Succeeded ? 0 : counts[provider] + 1;
                        failures[provider] = result.Failures.LastOrDefault();
                        if (result.Failures.Any(f => f.Kind == FailureKind.AccountMismatch))
                        {
                            verifiedSelections.Remove(provider);
                            state.Latest.RemoveAll(s => s.Key.Account.Provider == provider);
                        }
                        if (result.Succeeded && selected.PinnedAccount(provider) is null &&
                            provider is Provider.Copilot or Provider.Codex)
                        {
                            var keys = result.Snapshots.Select(s => s.Key.Account.Id).Distinct().ToArray();
                            if (keys.Length != 1) throw new UsageException(FailureKind.AccountMismatch, "usage_accounts_ambiguous");
                            settings = settings.PinAccount(provider, keys[0]);
                            await settings.SaveAsync(settingsPath, stopping.Token);
                            state.Bind(provider, settings.SelectionId(provider));
                        }
                        var retry = result.Failures.Select(f => f.RetryAfter ?? TimeSpan.Zero).DefaultIfEmpty().Max();
                        next[provider] = DateTimeOffset.UtcNow + Polling.NextDelay(state.Polling, counts[provider], retry);
                        int before = state.Deltas.Count;
                        var notices = UsageEngine.Apply(state, result, DateTimeOffset.UtcNow);
                        await RecordHistoryAsync(before, stopping.Token);
                        if (result.Succeeded) verifiedSelections.Add(provider);
                        await store!.SaveAsync(state, stopping.Token);
                        RefreshUsageStrip();
                        if (notices.Count > 0)
                            tray.Notify(Locale.Text(UiText.AgentUsage), string.Join("\n", notices.Select(NoticeText)));
                    }
                    finally { stateGate.Release(); }
                }
                await stateGate.WaitAsync(stopping.Token);
                try { await history!.RetryAsync(state.Polling.MaximumAge, stopping.Token); }
                finally { stateGate.Release(); }
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
            lastError = "usage_polling_stopped:" + ex.GetType().Name;
            diagnostics?.Write(HostEvent.PollingStopped, exception: ex);
            tray.Notify(Locale.Text(UiText.AgentUsage), Locale.Error(lastError), error: true);
        }
    }

    private async Task RecordHistoryAsync(int firstDelta, CancellationToken cancellationToken)
    {
        var records = state.Deltas.Skip(firstDelta).Select(delta =>
        {
            var snapshot = state.Latest.Single(s => s.Key == delta.Key && s.PeriodId == delta.PeriodId);
            return HistoryRecord.Create(snapshot, delta);
        }).ToArray();
        try { await history!.RecordAsync(records, state.Polling.MaximumAge, cancellationToken); }
        catch (Exception ex) when (ex is UsageException or IOException or UnauthorizedAccessException)
        {
            // Primary state still persists below; it remains the raw source if journal persistence fails.
            lastError = ex is UsageException usage ? usage.SafeCode : "history_local_journal_unavailable:" + ex.GetType().Name;
        }
    }

    private string NoticeText(NotificationCandidate n) => n.Reason switch
    {
        NotificationReason.DailyGuidance or NotificationReason.WeeklyGuidance => Locale.Format(UiText.NotificationGuidance,
            ConnectionText.Name(n.Key.Account.Provider), n.Key.Bucket,
            Locale.Text(n.Reason == NotificationReason.DailyGuidance ? UiText.Daily : UiText.Weekly),
            Locale.Coverage(n.Coverage ?? Coverage.Partial)),
        NotificationReason.Reset => Locale.Format(UiText.NotificationReset, ConnectionText.Name(n.Key.Account.Provider), n.Key.Bucket),
        _ => Locale.Format(UiText.NotificationUsage, ConnectionText.Name(n.Key.Account.Provider), n.Key.Bucket,
            string.Join(" / ", n.CrossedLevels.Select(level => level.ToString("0.##", Locale.Culture) + "%")))
    };

    private async Task ExitAsync(bool forUpdate = false, HostShutdownReason reason = HostShutdownReason.None)
    {
        if (exiting) return;
        exiting = true;
        shutdownReason = forUpdate ? HostShutdownReason.Update : reason;
        diagnostics?.Write(HostEvent.ShutdownRequested, shutdownReason);
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
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                exiting = false; updateReserved = false;
                diagnostics?.Write(HostEvent.ShutdownCancelled, shutdownReason);
                shutdownReason = HostShutdownReason.None;
                tray.Notify(Locale.Text(UiText.AgentUsage), Locale.Text(UiText.UnsavedConfirm), error: true);
                return;
            }
        }
        diagnostics?.Write(HostEvent.Cancelling, shutdownReason);
        stopping.Cancel();
        if (!forUpdate && updateProcess is { HasExited: false })
            await ReadOnlyProcess.CloseAsync(updateProcess);
        diagnostics?.Write(HostEvent.Draining, shutdownReason);
        if (polling is not null) await polling;
        if (server is not null) await server;
        if (discoveryRun is not null) await discoveryRun;
        diagnostics?.Write(HostEvent.Saving, shutdownReason);
        if (store is not null && initialized) await store.SaveAsync(state, CancellationToken.None);
        discoveryCancellation?.Dispose();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
        diagnostics?.Write(HostEvent.Disposing, shutdownReason);
        panel?.Dispose(); updateProcess?.Dispose(); usageStrip?.Dispose(); tray.Dispose(); http.Dispose();
        shutdownComplete = true;
        tray.Stop(shutdownReason == HostShutdownReason.InitializationFailed ? 1 : 0);
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
