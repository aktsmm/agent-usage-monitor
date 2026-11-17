using System.Text.Json;
using AiUsageTray.Core;
using AiUsageTray.Desktop;

static class HostLifetimeTests
{
    public static void Register(Action<string, Action> test)
    {
        test("native dialogs from the windowless update helper are forced to the foreground", () =>
        {
            foreach (var flags in new[] { NativeTray.ErrorDialog, NativeTray.InformationDialog, NativeTray.ConfirmDialog })
                if ((flags & 0x10000) == 0 || (flags & 0x40000) == 0)
                    throw new InvalidOperationException("dialog_not_foreground");
            if ((NativeTray.ConfirmDialog & 0xF) != 4 || (NativeTray.ConfirmDialog & 0xF0) != 32)
                throw new InvalidOperationException("confirm_style_changed");
        });

        test("state lease contention is distinct from other storage failures and stale lock files", () =>
        {
            WithProfile(profile =>
            {
                var path = Path.Combine(profile, "state.json");
                using (var owner = new StateStore(path))
                {
                    try
                    {
                        using var duplicate = new StateStore(path);
                        throw new InvalidOperationException("Duplicate lease acquired");
                    }
                    catch (StateStoreInUseException ex) when (ex.InnerException is IOException inner &&
                        (inner.HResult & 0xFFFF) == 32) { }
                }
                using (var reopened = new StateStore(path)) { }
                File.Delete(path + ".lock");
                Directory.CreateDirectory(path + ".lock");
                try
                {
                    using var invalid = new StateStore(path);
                    throw new InvalidOperationException("Invalid lease path accepted");
                }
                catch (UnauthorizedAccessException) { }
            });
        });
        test("sharing violations while reading state are not mistaken for a duplicate lease", () =>
        {
            WithProfile(profile =>
            {
                var path = Path.Combine(profile, "state.json");
                using var owner = new StateStore(path);
                using var blockedState = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    _ = owner.LoadAsync(default).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Locked state was read");
                }
                catch (StateStoreInUseException)
                { throw new InvalidOperationException("State read failure misclassified as duplicate"); }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32) { }
            });
        });
        test("delayed background duplicate exits zero without changing the owner's profile or diagnostics", () =>
        {
            WithProfile(profile =>
            {
                var path = Path.Combine(profile, "state.json");
                using var owner = new StateStore(path);
                File.WriteAllText(path, "synthetic-state-do-not-load");
                File.WriteAllText(Path.Combine(profile, "settings.json"), "synthetic-settings-do-not-load");
                var log = new HostDiagnostics(profile, "synthetic");
                log.Write(HostEvent.Ready);
                var before = Directory.GetFiles(profile, "*", SearchOption.AllDirectories)
                    .Where(file => file != path + ".lock")
                    .ToDictionary(file => file, File.ReadAllBytes);
                var previousContext = SynchronizationContext.Current;
                int previousExitCode = Environment.ExitCode;
                var previousError = Console.Error;
                using var error = new StringWriter();
                TrayHost? host = null;
                try
                {
                    Console.SetError(error);
                    Environment.ExitCode = 42;
                    host = new TrayHost(["--background", "--profile", profile]);
                    host.Run();
                    if (Environment.ExitCode != 0 || error.ToString().Trim() != "host_profile_in_use")
                        throw new InvalidOperationException("Duplicate did not exit explicitly and successfully");
                    if (Directory.GetFiles(profile, "*", SearchOption.AllDirectories).Length != before.Count + 1 ||
                        before.Any(file => !file.Value.SequenceEqual(File.ReadAllBytes(file.Key))))
                        throw new InvalidOperationException("Duplicate changed the owner's files");
                    try
                    {
                        using var duplicate = new StateStore(path);
                        throw new InvalidOperationException("Owner's lease was released");
                    }
                    catch (StateStoreInUseException) { }
                    log.Write(HostEvent.Heartbeat);
                }
                finally
                {
                    host?.ReleaseLease();
                    Console.SetError(previousError);
                    Environment.ExitCode = previousExitCode;
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            });
        });
        test("native dispatch faults return to managed caller instead of escaping WndProc", () =>
            DispatchFault(asynchronous: false));
        test("async void native context faults stop the loop explicitly", () =>
            DispatchFault(asynchronous: true));
        test("native message loop retains WM_QUIT exit code", () =>
        {
            using var tray = Tray();
            tray.Post(_ => tray.Stop(42), null);
            if (tray.Run() != 42) throw new InvalidOperationException("Quit code lost");
        });
        test("native fatal callback does not execute subsequent queued work", () =>
        {
            using var tray = Tray();
            bool continued = false;
            tray.Post(_ => tray.Fail(new InvalidOperationException("synthetic-terminal-fault")), null);
            tray.Post(_ => continued = true, null);
            try { tray.Run(); }
            catch (InvalidOperationException ex) when (ex.Message == "synthetic-terminal-fault")
            {
                if (continued) throw new InvalidOperationException("Continued after terminal fault");
                return;
            }
            throw new InvalidOperationException("Terminal fault lost");
        });
        test("host diagnostics redact messages paths data and inner exceptions", () =>
        {
            WithProfile(profile =>
            {
                var log = new HostDiagnostics(profile, "0.2.0");
                log.Write(HostEvent.Fatal, exception: new IOException(
                    "private@example.test C:\\private\\credentials token-secret",
                    new Exception("provider-response-secret")));
                var content = File.ReadAllText(Log(profile));
                foreach (var secret in new[] { "private@", "credentials", "token-secret", "provider-response", profile })
                    if (content.Contains(secret)) throw new InvalidOperationException("Sensitive evidence");
                if (!content.Contains("System.IO.IOException"))
                    throw new InvalidOperationException("Exception type missing");
            });
        });
        test("host heartbeat cannot be mistaken for a clean exit after forced termination", () =>
        {
            WithProfile(profile =>
            {
                new HostDiagnostics(profile, "0.2.0").Write(HostEvent.Heartbeat);
                var next = new HostDiagnostics(profile, "0.2.0");
                if (!Records(profile).Any(r => r.Event == HostEvent.PreviousRunIncomplete))
                    throw new InvalidOperationException("Interrupted run not detected");
                next.Write(HostEvent.CleanExit, HostShutdownReason.TrayMenu, exitCode: 0);
                var count = Records(profile).Count(r => r.Event == HostEvent.PreviousRunIncomplete);
                _ = new HostDiagnostics(profile, "0.2.0");
                if (Records(profile).Count(r => r.Event == HostEvent.PreviousRunIncomplete) != count)
                    throw new InvalidOperationException("Clean run misclassified");
            });
        });
        test("host evidence rotates within its byte bound and keeps latest marker", () =>
        {
            WithProfile(profile =>
            {
                var log = new HostDiagnostics(profile, "0.2.0");
                for (int i = 0; i < 1300; i++) log.Write(HostEvent.Heartbeat);
                log.Write(HostEvent.CleanExit, HostShutdownReason.WindowClose, exitCode: 0);
                foreach (var path in new[] { Log(profile), Log(profile) + ".previous" })
                    if (!File.Exists(path) || new FileInfo(path).Length > HostDiagnostics.MaximumLogBytes)
                        throw new InvalidOperationException("Evidence bound violated");
                var marker = JsonSerializer.Deserialize<HostRunRecord>(
                    File.ReadAllBytes(Path.Combine(profile, "diagnostics", "host-run.json")))!;
                if (marker.Event != HostEvent.CleanExit || marker.Reason != HostShutdownReason.WindowClose ||
                    marker.ExitCode != 0)
                    throw new InvalidOperationException("Exit marker lost");
            });
        });
    }

    private static NativeTray Tray() => new(() => { }, () => { }, () => { }, () => { }, _ => { },
        () => { }, () => { }, () => false);

    private static void DispatchFault(bool asynchronous)
    {
        using var tray = Tray();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NativeContext(tray));
        async void Callback(object? state)
        {
            await Task.Yield();
            throw new InvalidOperationException("synthetic-callback-fault");
        }
        try
        {
            if (asynchronous) tray.Post(Callback, null);
            else tray.Post(_ => throw new InvalidOperationException("synthetic-callback-fault"), null);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { tray.Run(); }
            catch (InvalidOperationException ex) when (ex.Message == "synthetic-callback-fault") { return; }
            throw new InvalidOperationException("Fault was silently resumed");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static string Log(string profile) => Path.Combine(profile, "diagnostics", "host.jsonl");
    private static HostRunRecord[] Records(string profile) => File.ReadAllLines(Log(profile))
        .Select(line => JsonSerializer.Deserialize<HostRunRecord>(line)!).ToArray();
    private static void WithProfile(Action<string> action)
    {
        var profile = Path.Combine(Path.GetTempPath(), "AiUsageTray-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try { action(profile); }
        finally { Directory.Delete(profile, recursive: true); }
    }
}
