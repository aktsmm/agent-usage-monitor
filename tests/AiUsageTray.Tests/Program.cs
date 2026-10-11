using System.Net;
using System.Text;
using System.Text.Json;
using AiUsageTray.Core;
using AiUsageTray.Diagnostics;
using AiUsageTray.Desktop;

if (args.Contains("--child-hang"))
{
    await Task.Delay(TimeSpan.FromMinutes(1));
    return 0;
}
if (args.Contains("--child-env"))
{
    Console.WriteLine(Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") is null ? "absent" : "present");
    return 0;
}
if (args.Contains("--child-immediate"))
    return 0;

var tests = new List<(string Name, Func<Task> Run)>();
var origin = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
var at = DateTimeOffset.Parse("2026-10-12T00:00:00+09:00");
var account = AccountKey.FromIdentity(Provider.Copilot, "synthetic-account");
QuotaSnapshot Sample(decimal used, DateTimeOffset? observed = null) =>
    new(new(account, "synthetic"), "synthetic-plan", UsageSource.CopilotHttp, observed ?? at,
        UsageUnit.PercentagePoints, used, 100, used, false,
        new(WindowKind.Monthly, origin, origin.AddMonths(1)));
FetchResult Result(params QuotaSnapshot[] snapshots) => new(snapshots, []);
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; actual {actual}");
}
void True(bool value)
{
    if (!value) throw new InvalidOperationException("Assertion failed");
}
void Schema(Action action)
{
    try { action(); }
    catch (UsageException ex) when (ex.Kind == FailureKind.Schema) { return; }
    throw new InvalidOperationException("Expected explicit schema error");
}
JsonElement Json(string content)
{
    using var doc = JsonDocument.Parse(content);
    return doc.RootElement.Clone();
}

Test("31-day monthly allocation is fixed", () =>
{
    var a = Budgets.Calculate(Sample(36))!;
    Equal(100m / 31, a.Daily);
    Equal(100m / 31 * 7, a.Weekly);
    Equal(a, Budgets.Calculate(Sample(80)));
});
Test("February leap-year allocation", () =>
{
    var sample = Sample(20) with { Window = new(WindowKind.Monthly,
        DateTimeOffset.Parse("2028-02-01T00:00:00Z"), DateTimeOffset.Parse("2028-03-01T00:00:00Z")) };
    Equal(100m / 29, Budgets.Calculate(sample)!.Daily);
});
Test("weekly points divide by seven", () =>
{
    var sample = Sample(20) with { Window = new(WindowKind.Weekly, at, at.AddDays(7)) };
    Equal(100m / 7, Budgets.Calculate(sample)!.Daily);
    Equal(100m, Budgets.Calculate(sample)!.Weekly);
});
Test("unknown denominator and unlimited are not invented", () =>
{
    Equal<Guidance?>(null, Budgets.Calculate(Sample(36) with { Total = null }));
    Equal<Guidance?>(null, Budgets.Calculate(Sample(36) with { Unlimited = true }));
    Equal<Guidance?>(null, Budgets.Calculate(Sample(36) with { Window = new(WindowKind.Short, at, at.AddHours(5)) }));
});
Test("JST calendar day and Monday week", () =>
{
    var sunday = DateTimeOffset.Parse("2026-10-11T14:59:59Z");
    Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(9)), JapaneseCalendar.WeekStart(sunday));
    Equal(at, JapaneseCalendar.DayStart(sunday.AddSeconds(1)));
    Equal(at, JapaneseCalendar.WeekStart(sunday.AddSeconds(1)));
});
Test("first observation is unmeasured, not zero", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(36)), at);
    var daily = Budgets.Summarize(state, Sample(36), false);
    Equal(Coverage.Unmeasured, daily.Coverage);
    Equal<decimal?>(null, daily.Measured);
});
Test("first observation today still leaves partial coverage", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(36, at.AddHours(1))), at.AddHours(1));
    UsageEngine.Apply(state, Result(Sample(40, at.AddHours(2))), at.AddHours(2));
    var daily = Budgets.Summarize(state, Sample(40, at.AddHours(2)), false);
    Equal(Coverage.Partial, daily.Coverage);
    Equal<decimal?>(4, daily.Measured);
});
Test("midnight baseline gives complete-to-observation coverage", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(36)), at);
    UsageEngine.Apply(state, Result(Sample(37, at.AddMinutes(5))), at.AddMinutes(5));
    Equal(Coverage.CompleteToObservation, Budgets.Summarize(state, Sample(37, at.AddMinutes(5)), false).Coverage);
});
Test("sleep crossing midnight is not allocated to today's usage", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(20, at.AddMinutes(-5))), at.AddMinutes(-5));
    UsageEngine.Apply(state, Result(Sample(40, at.AddMinutes(5))), at.AddMinutes(5));
    Equal<decimal?>(null, Budgets.Summarize(state, Sample(40, at.AddMinutes(5)), false).Measured);
    var newSample = Sample(42, at.AddMinutes(10));
    UsageEngine.Apply(state, Result(newSample), newSample.ObservedAt);
    var summary = Budgets.Summarize(state, newSample, false);
    Equal(Coverage.Partial, summary.Coverage);
    Equal<decimal?>(2, summary.Measured);
});
Test("adjustment is not negative consumption or reset", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(90)), at);
    var notices = UsageEngine.Apply(state, Result(Sample(20, at.AddMinutes(5))), at.AddMinutes(5));
    Equal(DeltaKind.Adjustment, state.Deltas.Last().Kind);
    Equal(0, notices.Count);
});
Test("confirmed reset only after previous reset time", () =>
{
    var state = new UsageState();
    var previous = Sample(90) with { ObservedAt = origin.AddMonths(1).AddMinutes(-5) };
    UsageEngine.Apply(state, Result(previous), previous.ObservedAt);
    var next = Sample(1) with { ObservedAt = origin.AddMonths(1).AddMinutes(5),
        Window = new(WindowKind.Monthly, origin.AddMonths(1), origin.AddMonths(2)) };
    var notices = UsageEngine.Apply(state, Result(next), next.ObservedAt);
    Equal(DeltaKind.Reset, state.Deltas.Last().Kind);
    Equal(1, notices.Count(n => n.Reason == NotificationReason.Reset));
});
Test("early reset-date change is a definition change", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(90)), at);
    var changed = Sample(1, at.AddMinutes(5)) with { Window = new(WindowKind.Monthly, origin, origin.AddMonths(2)) };
    Equal(0, UsageEngine.Apply(state, Result(changed), changed.ObservedAt).Count);
    Equal(DeltaKind.DefinitionChanged, state.Deltas.Last().Kind);
});
Test("plan or entitlement change is not consumption", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(10)), at);
    var changed = Sample(70, at.AddMinutes(5)) with { Plan = "changed", Total = 200 };
    Equal(0, UsageEngine.Apply(state, Result(changed), changed.ObservedAt).Count);
    Equal(DeltaKind.DefinitionChanged, state.Deltas.Last().Kind);
});
Test("multiple thresholds coalesce, reasons remain separate", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(70)), at);
    var notices = UsageEngine.Apply(state, Result(Sample(96, at.AddMinutes(5))), at.AddMinutes(5));
    Equal(1, notices.Count(n => n.Reason == NotificationReason.Threshold));
    Equal(3, notices.Single(n => n.Reason == NotificationReason.Threshold).CrossedLevels.Length);
    Equal(1, notices.Count(n => n.Reason == NotificationReason.PercentageStep));
    True(notices.Any(n => n.Reason == NotificationReason.DailyGuidance));
});
Test("exact daily guidance is not an exceedance", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(0)), at);
    var next = Sample(100m / 31, at.AddMinutes(5));
    True(UsageEngine.Apply(state, Result(next), next.ObservedAt).All(n => n.Reason != NotificationReason.DailyGuidance));
});
Test("different accounts and buckets are isolated", () =>
{
    var state = new UsageState();
    var first = Sample(90);
    var other = first with { Key = new(AccountKey.FromIdentity(Provider.Copilot, "other"), "synthetic") };
    var notices = UsageEngine.Apply(state, Result(first, other), at);
    Equal(2, notices.Count(n => n.Reason == NotificationReason.Threshold));
    Equal(2, state.Latest.Count);
});
Test("stale future and repeated observations do not notify", () =>
{
    var state = new UsageState();
    Equal(0, UsageEngine.Apply(state, Result(Sample(95, at.AddHours(-1))), at).Count);
    Equal(0, UsageEngine.Apply(state, Result(Sample(95, at.AddHours(1))), at).Count);
    UsageEngine.Apply(state, Result(Sample(20)), at);
    Equal(0, UsageEngine.Apply(state, Result(Sample(95)), at).Count);
    Equal(1, state.Deltas.Count);
});
Test("fetch failure preserves the last confirmed snapshot", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(20)), at);
    Equal(0, UsageEngine.Apply(state, FetchResult.Failed(new(FailureKind.Network, "synthetic", at.AddHours(1))), at.AddHours(1)).Count);
    True(!state.Latest.Single().IsFresh(at.AddHours(1), state.Polling.MaximumAge));
});
Test("month-crossing calendar week uses isolated period slices", () =>
{
    var sample = Sample(1, DateTimeOffset.Parse("2026-11-01T08:55:00+09:00"));
    var summary = Budgets.Summarize(new UsageState(), sample, true);
    Equal(origin.AddMonths(1), summary.EndsAt);
    True(summary.StartsAt >= origin);
    True(summary.Guidance < Budgets.Calculate(sample)!.Weekly);
});
Test("Copilot credits-only does not invent a credit denominator", () =>
{
    var parsed = QuotaParsers.Copilot(Json("""
        {"copilot_plan":"enterprise","quota_reset_date_utc":"2026-11-01T00:00:00Z",
        "quota_snapshots":{"premium_models":{"unlimited":true,"credits_used":123.5}}}
        """), account, at).Single();
    Equal(UsageUnit.Credits, parsed.Unit);
    Equal<decimal?>(123.5m, parsed.Used);
    Equal<decimal?>(null, parsed.Total);
    Equal<decimal?>(null, parsed.UsedPercent);
});
Test("Copilot percentage and credits preserve separate units", () =>
{
    var parsed = QuotaParsers.Copilot(Json("""
        {"quota_reset_date":"2026-11-01","quota_snapshots":{
        "premium_interactions":{"entitlement":300,"remaining":192,"percent_remaining":64,"credits_used":0}}}
        """), account, at).Single();
    Equal(UsageUnit.PercentagePoints, parsed.Unit);
    Equal<decimal?>(36m, parsed.Used);
    Equal<decimal?>(0m, parsed.CreditsUsed);
    Equal(100m / 31, Budgets.Calculate(parsed)!.Daily);
});
Test("unknown Copilot quota fields remain unknown", () =>
{
    var parsed = QuotaParsers.Copilot(Json("""{"quota_snapshots":{"new_shape":{}}}"""), account, at).Single();
    Equal(UsageUnit.Unknown, parsed.Unit);
    Equal<decimal?>(null, parsed.Used);
    Schema(() => QuotaParsers.Copilot(Json("{}"), account, at));
});
Test("schema type drift is explicit", () =>
{
    Schema(() => (Sample(1) with { Key = null! }).Validate());
    Schema(() => QuotaParsers.Copilot(Json("""{"quota_snapshots":{"x":{"percent_remaining":"64"}}}"""), account, at));
    Schema(() => QuotaParsers.CodexHttp(Json("""{"rate_limit":{"primary_window":{"used_percent":101}}}"""), account, at));
});
Test("Codex HTTP primary and weekly are separate", () =>
{
    var parsed = QuotaParsers.CodexHttp(Json("""
        {"plan_type":"plus","rate_limit":{
        "primary_window":{"used_percent":58,"limit_window_seconds":18000,"reset_at":1791756000},
        "secondary_window":{"used_percent":24,"limit_window_seconds":604800,"reset_at":1792015200}}}
        """), AccountKey.FromIdentity(Provider.Codex, "synthetic"), at);
    Equal(2, parsed.Count);
    Equal(WindowKind.Short, parsed[0].Window.Kind);
    Equal(WindowKind.Weekly, parsed[1].Window.Kind);
});
Test("Codex CLI multi-bucket view wins over legacy duplicate", () =>
{
    var parsed = QuotaParsers.CodexCli(Json("""
        {"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1791756000}},
        "rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1791756000}},
        "review":{"secondary":{"usedPercent":12,"windowDurationMins":10080,"resetsAt":1792015200}}}}
        """), account, "plus", at);
    Equal(2, parsed.Count);
    True(parsed.Any(x => x.Key.Bucket == "review:secondary"));
});
Test("Claude missing statusline is not zero", () =>
{
    var key = AccountKey.FromIdentity(Provider.Claude, "synthetic");
    Schema(() => QuotaParsers.ClaudeSnapshot(Json($$"""{"account_id":"{{key.Id}}","observed_at":"2026-10-12T00:00:00Z"}"""), key));
    var parsed = QuotaParsers.ClaudeSnapshot(Json("""
        {"account_id":"SYNTHETIC_ID","observed_at":"2026-10-12T00:00:00Z","rate_limits":{
        "five_hour":null,"seven_day":{"used_percentage":12,"resets_at":1792015200}}}
        """.Replace("SYNTHETIC_ID", key.Id)), key);
    Equal(1, parsed.Count);
    Equal(WindowKind.Weekly, parsed[0].Window.Kind);
});
Test("retry-after overrides exponential cap", () =>
{
    var options = new PollingOptions();
    Equal(TimeSpan.FromMinutes(5), Polling.NextDelay(options, 0, null));
    Equal(TimeSpan.FromMinutes(10), Polling.NextDelay(options, 1, null));
    Equal(TimeSpan.FromHours(1), Polling.NextDelay(options, 20, null));
    Equal(TimeSpan.FromHours(2), Polling.NextDelay(options, 2, TimeSpan.FromHours(2)));
});
Test("bad rules and duplicate buckets are rejected", () =>
{
    Schema(() => (new NotificationRules { PercentageStep = 0 }).Validate());
    Schema(() => (new NotificationRules { PercentageStep = 0.001m }).Validate());
    Schema(() => (new NotificationRules { Thresholds = [90, 90] }).Validate());
    Schema(() => UsageEngine.Apply(new(), Result(Sample(10), Sample(20)), at));
});
Test("retention preserves current-period deduplication", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(96)), at);
    var count = state.Notifications.Count;
    HistoryRetention.Prune(state, at.AddDays(100));
    Equal(count, state.Notifications.Count);
    Equal(0, state.Deltas.Count);
    Equal(1, state.Baselines.Count);
});
Test("selected account binding invalidates only changed-provider latest values", () =>
{
    var state = new UsageState();
    state.Bind(Provider.Copilot, "synthetic-old");
    UsageEngine.Apply(state, Result(Sample(50)), at);
    state.Bind(Provider.Copilot, "synthetic-old");
    Equal(1, state.Latest.Count);
    state.Bind(Provider.Copilot, "synthetic-new");
    Equal(0, state.Latest.Count);
    Equal(1, state.Deltas.Count);
});
Test("theme and notification editing do not change selected account binding", () =>
{
    var settings = new DesktopSettings { CopilotEnabled = true, GithubLogin = "synthetic" };
    Equal(settings.SelectionId(Provider.Copilot), (settings with { Theme = "Light" }).SelectionId(Provider.Copilot));
    True(settings.SelectionId(Provider.Copilot) != (settings with { GithubLogin = "different" }).SelectionId(Provider.Copilot));
});
Test("per-bucket notification disable does not erase measurements", () =>
{
    var state = new UsageState { QuotaRules = [new(Sample(95).Key, new(), false)] };
    Equal(0, UsageEngine.Apply(state, Result(Sample(95)), at).Count);
    Equal(1, state.Latest.Count);
});
Test("later baseline after interrupted selection stays partial", () =>
{
    var state = new UsageState();
    UsageEngine.Apply(state, Result(Sample(10)), at);
    UsageEngine.Apply(state, Result(Sample(11, at.AddMinutes(5))), at.AddMinutes(5));
    state.Latest.Clear();
    UsageEngine.Apply(state, Result(Sample(20, at.AddHours(1))), at.AddHours(1));
    UsageEngine.Apply(state, Result(Sample(21, at.AddHours(2))), at.AddHours(2));
    var summary = Budgets.Summarize(state, Sample(21, at.AddHours(2)), false);
    Equal(Coverage.Partial, summary.Coverage);
    Equal<decimal?>(2, summary.Measured);
});
Test("moving overlapping window is not a confirmed reset", () =>
{
    var state = new UsageState();
    var old = Sample(90) with { Window = new(WindowKind.Weekly, at.AddDays(-7), at) };
    old = old with { ObservedAt = at.AddMinutes(-5) };
    UsageEngine.Apply(state, Result(old), old.ObservedAt);
    var current = Sample(1, at.AddMinutes(5)) with { Window = new(WindowKind.Weekly, at.AddDays(-6), at.AddDays(1)) };
    True(UsageEngine.Apply(state, Result(current), current.ObservedAt).All(n => n.Reason != NotificationReason.Reset));
});
AsyncTest("Claude exporter retains only scalar quota fields", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"ai-usage-export-{Guid.NewGuid():N}.json");
    try
    {
        using var input = new StringReader("""
            {"session_id":"SYNTHETIC_SESSION","transcript_path":"SYNTHETIC_PRIVATE_PATH",
            "rate_limits":{"seven_day":{"used_percentage":12,"resets_at":1792015200}}}
            """);
        await ClaudeExporter.ExportAsync(input, "synthetic", path, default);
        var text = await File.ReadAllTextAsync(path);
        True(!text.Contains("SYNTHETIC_SESSION") && !text.Contains("transcript_path") && !text.Contains("SYNTHETIC_PRIVATE_PATH"));
        var parsed = QuotaParsers.ClaudeSnapshot(Json(text), AccountKey.FromIdentity(Provider.Claude, "synthetic"));
        Equal(1, parsed.Count);
    }
    finally { File.Delete(path); }
});
AsyncTest("hung child process is cancelled and closed", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
    try { await ReadOnlyProcess.CaptureAsync(Environment.ProcessPath!, ["--child-hang"], timeout.Token); }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return; }
    throw new InvalidOperationException("Expected bounded cancellation");
});
AsyncTest("unrelated API key is not inherited by helpers", async () =>
{
    var previous = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
    Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", "synthetic-not-a-secret");
    try
    {
        var text = await ReadOnlyProcess.CaptureAsync(Environment.ProcessPath!, ["--child-env"], default);
        Equal("absent", text.Trim());
    }
    finally { Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", previous); }
});
AsyncTest("already exited and exiting children close without a cleanup race", async () =>
{
    using (var process = ReadOnlyProcess.Start(Environment.ProcessPath!, ["--child-immediate"]))
    {
        await process.WaitForExitAsync();
        await ReadOnlyProcess.CloseAsync(process);
    }
    for (int i = 0; i < 5; i++)
    {
        using var process = ReadOnlyProcess.Start(Environment.ProcessPath!, ["--child-immediate"]);
        await Task.Delay(30);
        await ReadOnlyProcess.CloseAsync(process);
        True(process.HasExited);
    }
});
AsyncTest("persist and restart suppresses already reported notifications", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"ai-usage-test-{Guid.NewGuid():N}.json");
    try
    {
        using (var store = new StateStore(path))
        {
            var state = await store.LoadAsync(default);
            UsageEngine.Apply(state, Result(Sample(96)), at);
            await store.SaveAsync(state, default);
        }
        using (var store = new StateStore(path))
        {
            var state = await store.LoadAsync(default);
            Equal(0, UsageEngine.Apply(state, Result(Sample(96, at.AddMinutes(5))), at.AddMinutes(5)).Count);
        }
    }
    finally { File.Delete(path); File.Delete(path + ".lock"); }
});
AsyncTest("HTTP errors are classified without leaking response body", async () =>
{
    using var http = new UsageHttp(new FakeHandler(request =>
    {
        Equal(HttpMethod.Get, request.Method);
        return new(HttpStatusCode.TooManyRequests) {
            Content = new StringContent("SENSITIVE_RESPONSE_DO_NOT_PRINT"),
            Headers = { RetryAfter = new(System.TimeSpan.FromMinutes(12)) }
        };
    }));
    try
    {
        await http.GetAsync(new("https://api.github.com/user"), "token", "synthetic", default);
        throw new InvalidOperationException("Expected failure");
    }
    catch (UsageException ex)
    {
        Equal(FailureKind.RateLimited, ex.Kind);
        Equal("http_429", ex.SafeCode);
        Equal<TimeSpan?>(TimeSpan.FromMinutes(12), ex.RetryAfter);
    }
});
AsyncTest("HTTP endpoint host restriction", async () =>
{
    using var http = new UsageHttp(new FakeHandler(_ => throw new InvalidOperationException("Must not send")));
    try { await http.GetAsync(new("https://example.com/"), "token", "synthetic", default); }
    catch (UsageException ex) when (ex.Kind == FailureKind.Forbidden) { return; }
    throw new InvalidOperationException("Expected forbidden");
});
AsyncTest("bounded IPC request preserves typed settings without credentials", async () =>
{
    using var stream = new MemoryStream();
    var request = new DesktopRequest("save",
        new DesktopSettings { GithubLogin = "synthetic", Theme = "Light" },
        new() { PercentageStep = 5 }, [new(Sample(1).Key, new(), false)]);
    await PipeProtocol.WriteAsync(stream, request, default);
    stream.Position = 0;
    var decoded = await PipeProtocol.ReadAsync<DesktopRequest>(stream, default);
    Equal("save", decoded.Method);
    Equal("Light", decoded.Settings!.Theme);
    Equal<decimal?>(5, decoded.Defaults!.PercentageStep);
    Equal(false, decoded.QuotaRules!.Single().Enabled);
    True(!Encoding.UTF8.GetString(stream.ToArray()).Contains("access_token"));
});
AsyncTest("IPC view transports precomputed coverage rather than complete history", async () =>
{
    using var stream = new MemoryStream();
    var snapshot = Sample(1);
    var view = new DesktopView(new() { Latest = [snapshot] }, new(), new(), null,
        [new(snapshot.Key, Budgets.Summarize(new(), snapshot, false), Budgets.Summarize(new(), snapshot, true))], true);
    await PipeProtocol.WriteAsync(stream, new DesktopResponse(true, View: view), default);
    stream.Position = 0;
    var decoded = await PipeProtocol.ReadAsync<DesktopResponse>(stream, default);
    Equal(Coverage.Unmeasured, decoded.View!.Quotas.Single().Daily.Coverage);
    Equal<decimal?>(null, decoded.View.Quotas.Single().Daily.Measured);
    Equal(0, decoded.View.State.Deltas.Count);
    Equal(true, decoded.View.OpenSettings);
});
AsyncTest("IPC oversized frame is rejected before allocation", async () =>
{
    using var stream = new MemoryStream(BitConverter.GetBytes(PipeProtocol.MaximumMessageBytes + 1));
    try { await PipeProtocol.ReadAsync<DesktopRequest>(stream, default); }
    catch (UsageException ex) when (ex.Kind == FailureKind.Schema) { return; }
    throw new InvalidOperationException("Expected IPC size rejection");
});
AsyncTest("IPC truncated frame cannot become a successful request", async () =>
{
    using var stream = new MemoryStream(BitConverter.GetBytes(8));
    try { await PipeProtocol.ReadAsync<DesktopRequest>(stream, default); }
    catch (EndOfStreamException) { return; }
    throw new InvalidOperationException("Expected IPC truncation rejection");
});
Test("startup registration accepts only its owned install executable", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "synthetic-install");
    var executable = Path.Combine(root, "current", "AiUsageTray.Desktop.exe");
    var command = StartupRegistration.Command(executable);
    True(StartupRegistration.BelongsToInstallation(command, root));
    True(!StartupRegistration.BelongsToInstallation(StartupRegistration.Command(
        Path.Combine(Path.GetTempPath(), "other", "AiUsageTray.Desktop.exe")), root));
    True(!StartupRegistration.BelongsToInstallation(command + " --extra", root));
    True(!StartupRegistration.BelongsToInstallation("\"relative.exe\" --background", root));
    True(!StartupRegistration.BelongsToInstallation("\"C:\\other\\foreign.exe\" --background", root));
});
Test("update asset trust remains pinned to one repository", () =>
{
    True(UpdateFlow.TrustedDownload(new("https://github.com/aktsmm/ai-usage-monitor/releases/download/v0.1.0/releases.win.json")));
    True(!UpdateFlow.TrustedDownload(new("https://github.com/another/app/releases/download/v0.1.0/Setup.exe")));
    True(!UpdateFlow.TrustedDownload(new("https://github.com/aktsmm/ai-usage-tray/releases/download/v0.1.0/releases.win.json")));
    True(!UpdateFlow.TrustedDownload(new("http://github.com/aktsmm/ai-usage-monitor/releases/download/v0.1.0/Setup.exe")));
    True(!UpdateFlow.TrustedDownload(new("https://github.com.example.com/aktsmm/ai-usage-monitor/releases/download/v0.1.0/Setup.exe")));
    True(!UpdateFlow.TrustedDownload(new("https://github.com/aktsmm/ai-usage-monitor/releases/download/v0.1.0/Setup.exe?redirect=bad")));
});
AsyncTest("portable update check handles absent public release without claiming latest", async () =>
{
    using var client = new HttpClient(new FakeHandler(request =>
    {
        Equal("https://api.github.com/repos/aktsmm/ai-usage-monitor/releases/latest", request.RequestUri!.AbsoluteUri);
        True(request.Headers.Authorization is null);
        return new(HttpStatusCode.NotFound);
    }));
    var message = await UpdateFlow.PortableCheckAsync(client, default);
    True(message.Contains("確認できませんでした"));
});
AsyncTest("portable update check offers release page instead of replacing executable", async () =>
{
    using var client = new HttpClient(new FakeHandler(_ =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{"tag_name":"v99.0.0"}""") }));
    var message = await UpdateFlow.PortableCheckAsync(client, default);
    True(message.Contains("自動置換しません"));
});
AsyncTest("update network failure is explicit and not up-to-date", async () =>
{
    using var client = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.Forbidden)));
    try { await UpdateFlow.PortableCheckAsync(client, default); }
    catch (HttpRequestException) { return; }
    throw new InvalidOperationException("Expected explicit update failure");
});
Test("public demo contains synthetic accounts and no saved credentials", () =>
{
    var view = DemoView.Create(false);
    Equal(2, view.State.Latest.Count);
    True(view.State.Latest.All(s => s.Plan == "サンプル"));
    True(!view.Settings.CopilotEnabled && !view.Settings.CodexEnabled && !view.Settings.ClaudeEnabled);
    Equal("", view.Settings.GithubLogin);
    Equal("", view.Settings.CodexEmail);
});

int failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(response(request));
}
