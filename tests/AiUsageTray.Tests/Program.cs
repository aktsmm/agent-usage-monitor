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
    var secretNames = new[] { "TYPESAFE_API_KEY", "GH_TOKEN", "GITHUB_TOKEN",
        "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" };
    Console.WriteLine(secretNames.All(name => Environment.GetEnvironmentVariable(name) is null) ? "absent" : "present");
    return 0;
}
if (args.Contains("--child-immediate"))
    return 0;
if (args.Contains("app-server"))
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var request = JsonDocument.Parse(line);
        if (!request.RootElement.TryGetProperty("id", out var id)) continue;
        var method = request.RootElement.GetProperty("method").GetString();
        object result = method switch
        {
            "initialize" => new { userAgent = "synthetic" },
            "account/read" => new { account = new { type = "chatgpt", email = "sample@example.test", planType = "synthetic" } },
            "account/rateLimits/read" => new { rateLimits = new { primary = new
                { usedPercent = 25, windowDurationMins = 300, resetsAt = 1893456000 } } },
            _ => throw new InvalidOperationException("Unapproved RPC method")
        };
        var home = Environment.GetEnvironmentVariable("CODEX_HOME")!;
        if (method == "account/rateLimits/read" && File.Exists(Path.Combine(home, "synthetic-switch")))
        {
            var auth = await File.ReadAllTextAsync(Path.Combine(home, "auth.json"));
            await File.WriteAllTextAsync(Path.Combine(home, "auth.json"), auth.Replace("initial-id", "different-id"));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt32(), result }));
    }
    return 0;
}

int fixtureIndex = Array.IndexOf(args, "--create-synthetic-profile");
int captureIndex = Array.IndexOf(args, "--capture-strip-fixtures");
if (captureIndex >= 0 && captureIndex + 1 < args.Length)
{
    NativeStripRenderSmoke.Run(Path.GetFullPath(args[captureIndex + 1]));
    Console.WriteLine("synthetic_strip_render_ready");
    return 0;
}
if (fixtureIndex >= 0 && fixtureIndex + 1 < args.Length)
{
    var profile = Path.GetFullPath(args[fixtureIndex + 1]);
    Directory.CreateDirectory(profile);
    var now = JapaneseCalendar.DayStart(DateTimeOffset.UtcNow);
    var key = new QuotaKey(AccountKey.FromIdentity(Provider.Copilot, "synthetic-profile"), "monthly demo");
    var first = new QuotaSnapshot(key, "Synthetic", UsageSource.CopilotHttp, now,
        UsageUnit.PercentagePoints, 20, 100, 20, false, new(WindowKind.Monthly, now.AddDays(-11), now.AddDays(20)));
    var state = new UsageState();
    UsageEngine.Apply(state, new([first], []), now);
    UsageEngine.Apply(state, new([first with { ObservedAt = now.AddMinutes(5), Used = 22, UsedPercent = 22 }], []), now.AddMinutes(5));
    using (var store = new StateStore(Path.Combine(profile, "state.json"))) await store.SaveAsync(state, default);
    await new DesktopSettings { CopilotMode = ConnectionMode.Disabled, CodexMode = ConnectionMode.Disabled,
        ClaudeMode = ConnectionMode.Disabled, Language = DisplayLanguage.English, AutoUpdateEnabled = false,
        UsageStripEnabled = true, CodexHome = Path.Combine(profile, "synthetic-codex") }
        .SaveAsync(Path.Combine(profile, "settings.json"), default);
    Console.WriteLine("synthetic_profile_ready");
    return 0;
}

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
    Equal(1, state.Deltas.Count);
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
AsyncTest("unrelated API and GitHub tokens are not inherited by helpers", async () =>
{
    var previous = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
    var previousGithub = Environment.GetEnvironmentVariable("GH_TOKEN");
    var previousGithubLegacy = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    var previousEnterprise = Environment.GetEnvironmentVariable("GH_ENTERPRISE_TOKEN");
    var previousEnterpriseLegacy = Environment.GetEnvironmentVariable("GITHUB_ENTERPRISE_TOKEN");
    Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", "synthetic-not-a-secret");
    Environment.SetEnvironmentVariable("GH_TOKEN", "synthetic-github-token");
    Environment.SetEnvironmentVariable("GITHUB_TOKEN", "synthetic-github-token");
    Environment.SetEnvironmentVariable("GH_ENTERPRISE_TOKEN", "synthetic-enterprise-token");
    Environment.SetEnvironmentVariable("GITHUB_ENTERPRISE_TOKEN", "synthetic-enterprise-token");
    try
    {
        var text = await ReadOnlyProcess.CaptureAsync(Environment.ProcessPath!, ["--child-env"], default);
        Equal("absent", text.Trim());
    }
    finally
    {
        Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", previous);
        Environment.SetEnvironmentVariable("GH_TOKEN", previousGithub);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousGithubLegacy);
        Environment.SetEnvironmentVariable("GH_ENTERPRISE_TOKEN", previousEnterprise);
        Environment.SetEnvironmentVariable("GITHUB_ENTERPRISE_TOKEN", previousEnterpriseLegacy);
    }
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
    True(UpdateFlow.TrustedDownload(new("https://github.com/aktsmm/agent-usage-monitor/releases/download/v0.1.0/releases.win.json")));
    True(!UpdateFlow.TrustedDownload(new("https://github.com/aktsmm/ai-usage-monitor/releases/download/v0.1.0/releases.win.json")));
    True(!UpdateFlow.TrustedDownload(new("https://github.com/another/app/releases/download/v0.1.0/Setup.exe")));
    True(!UpdateFlow.TrustedDownload(new("https://github.com/aktsmm/ai-usage-tray/releases/download/v0.1.0/releases.win.json")));
    True(!UpdateFlow.TrustedDownload(new("http://github.com/aktsmm/agent-usage-monitor/releases/download/v0.1.0/Setup.exe")));
    True(!UpdateFlow.TrustedDownload(new("https://github.com.example.com/aktsmm/agent-usage-monitor/releases/download/v0.1.0/Setup.exe")));
    True(!UpdateFlow.TrustedDownload(new("https://github.com/aktsmm/agent-usage-monitor/releases/download/v0.1.0/Setup.exe?redirect=bad")));
});
AsyncTest("portable update check handles absent public release without claiming latest", async () =>
{
    using var client = new HttpClient(new FakeHandler(request =>
    {
        Equal("https://api.github.com/repos/aktsmm/agent-usage-monitor/releases/latest", request.RequestUri!.AbsoluteUri);
        True(request.Headers.Authorization is null);
        return new(HttpStatusCode.NotFound);
    }));
    foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
    {
        var locale = new UiLocalizer(language);
        var message = await UpdateFlow.PortableCheckAsync(client, default, locale);
        True(message.Contains(locale.Text(UiText.UpdateUnavailable)));
        True(!message.Contains(locale.Format(UiText.UpdateCurrent, UpdateFlow.Version)));
    }
});
AsyncTest("portable update check offers release page instead of replacing executable", async () =>
{
    using var client = new HttpClient(new FakeHandler(_ =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{"tag_name":"v99.0.0","draft":false,"prerelease":false}""") }));
    foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
    {
        var locale = new UiLocalizer(language);
        var message = await UpdateFlow.PortableCheckAsync(client, default, locale);
        True(message.Contains(locale.Text(UiText.PortableUpdate)));
        True(message.Contains(locale.Format(UiText.UpdateAvailable, "99.0.0")));
    }
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

HistoryTests.Add(tests);
LocalizationTests.Add(tests);
var screenshotIndex = Array.IndexOf(args, "--history-smoke-output");
AsyncTest("history WPF graph and generated-file navigation render on demand", () => HistoryWindowSmoke.RunAsync(
    screenshotIndex >= 0 && screenshotIndex + 1 < args.Length ? args[screenshotIndex + 1] : null));

ConnectionProbe Verified(Provider provider) => new(provider, DiscoveryStatus.Ready, "synthetic_verified",
    provider == Provider.Copilot ? "sample-user" : "sample@example.test",
    AccountKey.FromIdentity(provider, "synthetic-id").Id, @"C:\Example\.codex", Environment.ProcessPath!);

Test("new profile is eligible once and automatic updates start enabled", () =>
{
    var settings = new DesktopSettings();
    True(settings.AutoUpdateEnabled);
    True(Enum.GetValues<Provider>().All(p => settings.Mode(p) == ConnectionMode.Pending));
    var selected = settings.WithMode(Provider.Copilot, ConnectionMode.Attempted);
    var connected = selected.CompleteDiscovery(selected, Verified(Provider.Copilot), true);
    True(connected.CopilotEnabled);
    Equal(ConnectionMode.Automatic, connected.CopilotMode);
    Equal("sample-user", connected.GithubLogin);
    Equal(Verified(Provider.Copilot).AccountKey, connected.GithubPinnedAccount);
});
Test("legacy defaults require one confirmation instead of silently enabling", () =>
{
    var legacy = DesktopSettings.MigrateLegacy(new());
    True(Enum.GetValues<Provider>().All(p => legacy.Mode(p) == ConnectionMode.NeedsConfirmation));
    var result = legacy.CompleteDiscovery(legacy, Verified(Provider.Copilot), true);
    True(!result.CopilotEnabled);
    Equal(ConnectionMode.NeedsConfirmation, result.CopilotMode);
});
Test("legacy manual disabled identity and customized paths are preserved", () =>
{
    var old = new DesktopSettings { GithubLogin = "sample-user", CodexHome = @"C:\Example\custom",
        Theme = "Light", StartAtLogon = true, ClaudeSnapshot = @"C:\Example\scalar.json" };
    var migrated = DesktopSettings.MigrateLegacy(old);
    Equal(ConnectionMode.Manual, migrated.CopilotMode);
    Equal(ConnectionMode.Manual, migrated.CodexMode);
    Equal(ConnectionMode.Manual, migrated.ClaudeMode);
    Equal(old.GithubLogin, migrated.GithubLogin);
    Equal(old.CodexHome, migrated.CodexHome);
    Equal("Light", migrated.Theme);
    True(migrated.StartAtLogon && !migrated.CopilotEnabled);
});
Test("redetection never enables disabled or manual service", () =>
{
    foreach (var mode in new[] { ConnectionMode.Disabled, ConnectionMode.Manual, ConnectionMode.Automatic })
    {
        var saved = new DesktopSettings { CopilotMode = mode, GithubLogin = "saved-user" };
        var result = saved.CompleteDiscovery(saved, Verified(Provider.Copilot), false);
        Equal(saved.CopilotEnabled, result.CopilotEnabled);
        Equal("saved-user", result.GithubLogin);
        Equal(mode, result.CopilotMode);
    }
});
Test("an in-flight detection cannot overwrite a user edit or opt-out", () =>
{
    var selection = new DesktopSettings { CopilotMode = ConnectionMode.Attempted };
    var edited = selection with { GithubLogin = "manual", CopilotMode = ConnectionMode.Manual };
    Equal("manual", edited.CompleteDiscovery(selection, Verified(Provider.Copilot), true).GithubLogin);
    var disabled = selection with { CopilotMode = ConnectionMode.Disabled };
    True(!disabled.CompleteDiscovery(selection, Verified(Provider.Copilot), true).CopilotEnabled);
});
Test("discovery failure never fabricates an enabled provider", () =>
{
    var selection = new DesktopSettings { CopilotMode = ConnectionMode.Attempted };
    foreach (var status in new[] { DiscoveryStatus.Failed, DiscoveryStatus.LoginRequired, DiscoveryStatus.Timeout,
        DiscoveryStatus.Cancelled, DiscoveryStatus.Unsupported })
    {
        var failed = selection.CompleteDiscovery(selection, new(Provider.Copilot, status, "synthetic"), true);
        True(!failed.CopilotEnabled && failed.GithubPinnedAccount is null);
        Equal(status, failed.ConnectionResults[Provider.Copilot].Status);
    }
});
Test("manual edits and disabling distinguish selection from appearance", () =>
{
    var automatic = new DesktopSettings().ApplyProbe(Verified(Provider.Copilot));
    var appearance = ConnectionEdits.Apply(automatic, automatic with { Theme = "Light", AutoUpdateEnabled = false });
    Equal(automatic.GithubPinnedAccount, appearance.GithubPinnedAccount);
    Equal(ConnectionMode.Automatic, appearance.CopilotMode);
    var disabled = ConnectionEdits.Apply(automatic, automatic with { CopilotEnabled = false });
    Equal(ConnectionMode.Disabled, disabled.CopilotMode);
    Equal(automatic.GithubPinnedAccount, disabled.GithubPinnedAccount);
    var manual = ConnectionEdits.Apply(automatic, automatic with { GithubLogin = "different" });
    Equal(ConnectionMode.Manual, manual.CopilotMode);
    True(manual.GithubPinnedAccount is null);
});
Test("Codex automatic fallback is enabled only with a resolved executable", () =>
{
    var detected = new DesktopSettings().ApplyProbe(Verified(Provider.Codex));
    True(detected.CodexEnabled && detected.CodexCliFallback);
    var direct = new DesktopSettings().ApplyProbe(Verified(Provider.Codex) with { Executable = "" });
    True(direct.CodexEnabled && !direct.CodexCliFallback);
});
Test("legacy last confirmed identity remains pinned before first fetch", () =>
{
    var old = new DesktopSettings { CopilotMode = ConnectionMode.Manual, GithubLogin = "sample-user" };
    var state = new UsageState { Latest = [Sample(30)] };
    Equal(account.Id, old.PinExisting(state).GithubPinnedAccount);
});
AsyncTest("restart retains disabled choices, detected pin and update opt-out without tokens", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), $"ai-usage-settings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(home);
    try
    {
        var saved = new DesktopSettings().ApplyProbe(Verified(Provider.Copilot)) with
        { CodexMode = ConnectionMode.Disabled, AutoUpdateEnabled = false, Theme = "Light" };
        var path = Path.Combine(home, "settings.json");
        await saved.SaveAsync(path, default);
        var restarted = await DesktopSettings.LoadAsync(path, default);
        Equal(ConnectionMode.Automatic, restarted.CopilotMode);
        Equal(ConnectionMode.Disabled, restarted.CodexMode);
        True(!restarted.AutoUpdateEnabled);
        Equal(saved.GithubPinnedAccount, restarted.GithubPinnedAccount);
        True(!File.ReadAllText(path).Contains("access_token"));
    }
    finally { Directory.Delete(home, true); }
});
Test("GitHub status uses one active account and rejects ambiguity", () =>
{
    Equal("sample-user", OfficialCli.ActiveGithubLogin(Json("""
        {"hosts":{"github.com":[{"active":true,"state":"success","login":"sample-user"},
        {"active":false,"state":"success","login":"inactive"}]}}
        """)));
    foreach (var json in new[] { """{"hosts":{"github.com":[]}}""",
        """{"hosts":{"github.com":[{"active":true,"state":"success","login":"a"},{"active":true,"state":"success","login":"b"}]}}""" })
    {
        try { OfficialCli.ActiveGithubLogin(Json(json)); }
        catch (UsageException ex) when (ex.Kind == FailureKind.AccountMismatch) { continue; }
        throw new InvalidOperationException("Expected ambiguous active selection");
    }
});
Test("GitHub account discovery returns usable unique logins with active first", () =>
{
    var candidates = OfficialCli.GithubLoginCandidates(Json("""
        {"hosts":{"github.com":[
        {"active":false,"state":"success","login":"other-user"},
        {"active":true,"state":"success","login":"active-user"},
        {"active":false,"state":"success","login":"ACTIVE-USER"},
        {"active":false,"state":"error","login":"expired-user"},
        {"active":false,"state":"success","login":"unusable-user"},
        {"active":false,"state":"error","login":"UNUSABLE-USER"}]}}
        """));
    Equal(2, candidates.Count);
    Equal("active-user", candidates[0].Login);
    True(candidates[0].Active);
    Equal("other-user", candidates[1].Login);
    True(!candidates[1].Active);
});
AsyncTest("GitHub account discovery reads status without requesting credentials", async () =>
{
    string[]? captured = null;
    var candidates = await OfficialCli.DiscoverGithubAccountsAsync(Environment.ProcessPath!, default,
        (_, args, _) =>
        {
            captured = args;
            return Task.FromResult((0, """{"hosts":{"github.com":[{"active":true,"state":"success","login":"sample-user"}]}}"""));
        });
    True(captured is not null && captured.SequenceEqual(["auth", "status", "--hostname", "github.com", "--json", "hosts"]));
    Equal("sample-user", candidates.Single().Login);
});
AsyncTest("VS Code bridge discovers fresh accounts and reads quota-only snapshots", async () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"ai-usage-vscode-bridge-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var observedAt = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(directory, "accounts.json"), JsonSerializer.Serialize(new
        {
            version = 1,
            accounts = new[] { new { login = "sample-user", userId = "42", label = "VS Code account",
                observedAt = observedAt.ToString("O") } }
        }));
        await File.WriteAllTextAsync(Path.Combine(directory, "42.json"), JsonSerializer.Serialize(new
        {
            version = 1,
            login = "sample-user",
            userId = "42",
            observedAt = observedAt.ToString("O"),
            usage = new { quota_reset_date = "2026-11-01",
                quota_snapshots = new { premium = new { percent_remaining = 64 } } }
        }));
        var candidates = await VscodeCopilotBridge.DiscoverAccountsAsync(directory);
        Equal("sample-user", candidates.Single().Login);
        var provider = new VscodeCopilotProvider("sample-user", "42",
            AccountKey.FromIdentity(Provider.Copilot, "github.com:42").Id, directory);
        var result = await provider.FetchAsync(default);
        True(result.Succeeded);
        Equal(36m, result.Snapshots.Single().Used);
        var mismatch = new VscodeCopilotProvider("other-user", "42", snapshotDirectory: directory)
            .FetchAsync(default);
        var failed = await mismatch;
        True(failed.Failures.Any(failure => failure.Kind == FailureKind.AccountMismatch));

        await File.WriteAllTextAsync(Path.Combine(directory, "accounts.json"), JsonSerializer.Serialize(new
        {
            version = 1,
            accounts = new[] { new { login = "stale-user", userId = "43", label = "stale",
                observedAt = observedAt.AddHours(-1).ToString("O") } }
        }));
        Equal(0, (await VscodeCopilotBridge.DiscoverAccountsAsync(directory)).Count);
    }
    finally { Directory.Delete(directory, true); }
});
AsyncTest("Copilot reads a non-active gh account by explicit login and validates API identity", async () =>
{
    var commands = new List<string[]>();
    var status = """{"hosts":{"github.com":[{"active":true,"state":"success","login":"active-user"},{"active":false,"state":"success","login":"inactive-user"},{"active":true,"state":"success","login":"inactive-user"}]}}""";
    using var http = new UsageHttp(new FakeHandler(request =>
    {
        Equal("synthetic-token", request.Headers.Authorization?.Parameter);
        return new(HttpStatusCode.OK) { Content = new StringContent(
            request.RequestUri!.AbsolutePath == "/user"
                ? """{"id":84,"login":"inactive-user"}"""
                : """{"quota_reset_date":"2026-11-01","quota_snapshots":{"premium":{"percent_remaining":64}}}""") };
    }));
    var provider = new CopilotProvider(http, "inactive-user", expectedAccount:
        AccountKey.FromIdentity(Provider.Copilot, "github.com:84").Id, executable: Environment.ProcessPath,
        commands: (_, args, _) =>
        {
            commands.Add(args);
            return Task.FromResult((0, args[1] == "status" ? status : "synthetic-token"));
        });
    var result = await provider.FetchAsync(default);
    True(result.Succeeded);
    True(commands[0].SequenceEqual(["auth", "status", "--hostname", "github.com", "--json", "hosts"]));
    True(commands[1].SequenceEqual(["auth", "token", "--hostname", "github.com", "--user", "inactive-user"]));
    True(!JsonSerializer.Serialize(result).Contains("synthetic-token"));
});
Test("CLI resolution excludes current directory and shell shims", () =>
{
    True(OfficialCli.Resolve("gh", ".") is null);
    try { OfficialCli.ValidateExecutable("codex.cmd"); }
    catch (UsageException ex) when (ex.Kind == FailureKind.Cli) { return; }
    throw new InvalidOperationException("Expected absolute executable guard");
});
AsyncTest("full Copilot detection verifies active login and API quota without CLI trials", async () =>
{
    var commands = new List<string>();
    using var http = new UsageHttp(new FakeHandler(request => new(HttpStatusCode.OK)
    {
        Content = new StringContent(request.RequestUri!.AbsolutePath == "/user"
            ? """{"id":42,"login":"sample-user"}"""
            : """{"quota_reset_date":"2026-11-01","quota_snapshots":{"premium":{"percent_remaining":64}}}""")
    }));
    var discovery = new ConnectionDiscovery(http, _ => Environment.ProcessPath!,
        (_, arguments, _) =>
        {
            commands.Add(string.Join(" ", arguments));
            return Task.FromResult((0, arguments[1] == "token" ? "synthetic-token" :
                """{"hosts":{"github.com":[{"active":true,"state":"success","login":"sample-user"}]}}"""));
        });
    var result = await discovery.ProbeAsync(Provider.Copilot, @"C:\Example\.codex", default);
    Equal(DiscoveryStatus.Ready, result.Status);
    Equal(AccountKey.FromIdentity(Provider.Copilot, "github.com:42").Id, result.AccountKey);
    True(result.Usage!.Succeeded);
    Equal(1, commands.Count(command => command.StartsWith("auth token")));
    True(commands.Where(command => command.StartsWith("auth status")).All(command => command.Contains("--active")));
    True(!JsonSerializer.Serialize(result).Contains("synthetic-token"));
});
AsyncTest("Claude logged-out exit one is login-required, never CLI-missing or zero", async () =>
{
    using var http = new UsageHttp(new FakeHandler(_ => throw new InvalidOperationException("No HTTP")));
    var discovery = new ConnectionDiscovery(http, _ => Environment.ProcessPath!,
        (_, arguments, _) => Task.FromResult(arguments[0] == "--version"
            ? (0, "2.1.999 (synthetic)") : (1, """{"loggedIn":false}""")));
    var result = await discovery.ProbeAsync(Provider.Claude, @"C:\Example\.codex", default);
    Equal(DiscoveryStatus.LoginRequired, result.Status);
    Equal("claude_login_required", result.Code);
    True(result.Usage is null && result.Executable.Length > 0);
});
AsyncTest("cancelled discovery has an explicit cancelled state", async () =>
{
    using var http = new UsageHttp(new FakeHandler(_ => throw new InvalidOperationException("No HTTP")));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    var discovery = new ConnectionDiscovery(http, _ => Environment.ProcessPath!,
        async (_, _, token) => { await Task.Delay(10000, token); return (0, ""); });
    Equal(DiscoveryStatus.Cancelled,
        (await discovery.ProbeAsync(Provider.Claude, @"C:\Example\.codex", cancellation.Token)).Status);
});
AsyncTest("GitHub SSO denial is distinct from missing auth and never enables service", async () =>
{
    using var http = new UsageHttp(new FakeHandler(_ => new(HttpStatusCode.Forbidden)));
    var discovery = new ConnectionDiscovery(http, _ => Environment.ProcessPath!,
        (_, arguments, _) => Task.FromResult((0, arguments[1] == "token" ? "synthetic-token" :
            """{"hosts":{"github.com":[{"active":true,"state":"success","login":"sample-user"}]}}""")));
    var result = await discovery.ProbeAsync(Provider.Copilot, @"C:\Example\.codex", default);
    Equal(DiscoveryStatus.Failed, result.Status);
    Equal(FailureKind.Forbidden, result.Failure!.Kind);
});
AsyncTest("Codex same email with a different account id is rejected before HTTP", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), $"ai-usage-auth-{Guid.NewGuid():N}");
    Directory.CreateDirectory(home);
    try
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"email":"sample@example.test"}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await File.WriteAllTextAsync(Path.Combine(home, "auth.json"), JsonSerializer.Serialize(new
        { auth_mode = "chatgpt", tokens = new { access_token = "synthetic", account_id = "new-id", id_token = $"x.{payload}.x" } }));
        using var http = new UsageHttp(new FakeHandler(_ => throw new InvalidOperationException("Must not fetch wrong account")));
        var result = await new CodexProvider(http, home, "sample@example.test", "", false,
            AccountKey.FromIdentity(Provider.Codex, "old-id").Id).FetchAsync(default);
        Equal(FailureKind.AccountMismatch, result.Failures.Single().Kind);
        Equal(0, result.Snapshots.Count);
        await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), """cli_auth_credentials_store = "keyring" """);
        result = await new CodexProvider(http, home, "sample@example.test", "", false).FetchAsync(default);
        Equal("codex_credential_store_unsupported", result.Failures.Single().Code);
    }
    finally { Directory.Delete(home, true); }
});
Test("automatic apply requires installer, opt-in and no open UI or discovery", () =>
{
    True(UpdatePolicy.CanApply(true, true, false, false, true, false));
    True(!UpdatePolicy.CanApply(false, true, false, false, true, false));
    True(!UpdatePolicy.CanApply(true, true, true, false, true, false));
    True(!UpdatePolicy.CanApply(true, true, false, true, true, false));
    True(!UpdatePolicy.CanApply(true, true, false, false, true, true));
    True(!UpdatePolicy.CanApply(true, true, false, false, false, false));
    True(UpdatePolicy.CanApply(false, false, false, false, true, false));
});
Test("update manifest binds package, stable version, size and SHA256", () =>
{
    if (!Velopack.SemanticVersion.TryParse("0.1.2", out var latest) || latest is null ||
        !Velopack.SemanticVersion.TryParse("0.1.1", out var current) || current is null)
        throw new InvalidOperationException("Expected stable semantic versions");
    var asset = new Velopack.VelopackAsset { PackageId = "AiUsageTrayDesktop",
        Version = latest, FileName = "AiUsageTrayDesktop-0.1.2-full.nupkg",
        Size = 1024, SHA256 = new string('a', 64) };
    UpdateFlow.ValidateAsset(asset, latest, current);
    foreach (var invalid in new[] {
        new Velopack.VelopackAsset { PackageId = "Foreign", Version = asset.Version, FileName = asset.FileName, Size = 1024, SHA256 = asset.SHA256 },
        new Velopack.VelopackAsset { PackageId = asset.PackageId, Version = asset.Version, FileName = "..\\foreign.nupkg", Size = 1024, SHA256 = asset.SHA256 },
        new Velopack.VelopackAsset { PackageId = asset.PackageId, Version = asset.Version, FileName = asset.FileName, Size = 1024, SHA256 = "" } })
    {
        try { UpdateFlow.ValidateAsset(invalid, latest, current); }
        catch (UsageException ex) when (ex.Kind == FailureKind.Forbidden) { continue; }
        throw new InvalidOperationException("Expected rejected manifest");
    }
    try { UpdateFlow.ValidateAsset(asset, latest, latest); }
    catch (UsageException ex) when (ex.Kind == FailureKind.Forbidden) { return; }
    throw new InvalidOperationException("Expected no same-version reapply");
});
AsyncTest("draft, prerelease and malformed update versions are never auto-applied", async () =>
{
    foreach (var json in new[] { """{"tag_name":"v0.1.2","draft":true,"prerelease":false}""",
        """{"tag_name":"v0.1.2-beta","draft":false,"prerelease":true}""",
        """{"tag_name":"garbage","draft":false,"prerelease":false}""" })
    {
        using var client = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) }));
        try { await UpdateFlow.LatestVersionAsync(client, default); }
        catch (UsageException ex) when (ex.Kind == FailureKind.Schema) { continue; }
        throw new InvalidOperationException("Expected rejected update metadata");
    }
});
Test("update downloader restricts repository and redirects without accepting foreign sources", () =>
{
    True(UpdateDownloader.TrustedSource(new("https://api.github.com/repos/aktsmm/agent-usage-monitor/releases?per_page=10")));
    True(UpdateDownloader.TrustedSource(new("https://github.com/aktsmm/agent-usage-monitor/releases/download/v0.1.2/releases.win.json")));
    True(UpdateDownloader.TrustedSource(new("https://release-assets.githubusercontent.com/example?signature=synthetic"), true));
    True(!UpdateDownloader.TrustedSource(new("https://api.github.com/repos/foreign/app/releases")));
    True(!UpdateDownloader.TrustedSource(new("https://example.com/package.nupkg"), true));
    True(!UpdateDownloader.TrustedSource(new("http://release-assets.githubusercontent.com/example"), true));
});
AsyncTest("Codex bounded RPC fallback stays on the pinned account and closes its child", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), $"ai-usage-rpc-{Guid.NewGuid():N}");
    Directory.CreateDirectory(home);
    try
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"email":"sample@example.test"}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var auth = JsonSerializer.Serialize(new { auth_mode = "chatgpt", tokens = new
            { access_token = "synthetic", account_id = "initial-id", id_token = $"x.{payload}.x" } });
        await File.WriteAllTextAsync(Path.Combine(home, "auth.json"), auth);
        using var http = new UsageHttp(new FakeHandler(_ => new(HttpStatusCode.Unauthorized)));
        var provider = new CodexProvider(http, home, "sample@example.test", Environment.ProcessPath!,
            true, AccountKey.FromIdentity(Provider.Codex, "initial-id").Id);
        var result = await provider.FetchAsync(default);
        True(result.Succeeded);
        Equal(UsageSource.CodexCli, result.Snapshots.Single().Source);
        Equal("direct_http_401", result.Failures.Single().Code);
        await File.WriteAllTextAsync(Path.Combine(home, "synthetic-switch"), "");
        result = await provider.FetchAsync(default);
        True(!result.Succeeded);
        Equal(FailureKind.AccountMismatch, result.Failures.Single().Kind);
        Equal("codex_cli_account_changed", result.Failures.Single().Code);
    }
    finally { Directory.Delete(home, true); }
});
AsyncTest("GitHub account change during discovery never returns old Usage", async () =>
{
    int checks = 0;
    using var http = new UsageHttp(new FakeHandler(request => new(HttpStatusCode.OK) { Content =
        new StringContent(request.RequestUri!.AbsolutePath == "/user" ? """{"id":42,"login":"sample-user"}""" :
        """{"quota_snapshots":{"premium":{"percent_remaining":64}}}""") }));
    var discovery = new ConnectionDiscovery(http, _ => Environment.ProcessPath!, (_, arguments, _) =>
    {
        if (arguments[1] == "token") return Task.FromResult((0, "synthetic-token"));
        checks++;
        return Task.FromResult((0, checks == 1
            ? """{"hosts":{"github.com":[{"active":true,"state":"success","login":"sample-user"}]}}"""
            : """{"hosts":{"github.com":[{"active":true,"state":"success","login":"different"}]}}"""));
    });
    var result = await discovery.ProbeAsync(Provider.Copilot, @"C:\Example\.codex", default);
    Equal(FailureKind.AccountMismatch, result.Failure!.Kind);
    True(result.Usage is null && result.Status != DiscoveryStatus.Ready);
});
AsyncTest("Claude discovery binds auth email to the default snapshot and auto-applies", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), $"ai-usage-claude-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try
    {
        var file = Path.Combine(dir, "claude-snapshot.json");
        var key = AccountKey.FromIdentity(Provider.Claude, "sample@example.com");
        await File.WriteAllTextAsync(file, """{"account_id":"KEY","observed_at":"2026-10-19T00:00:00Z","rate_limits":{"five_hour":{"used_percentage":1,"resets_at":1792377000},"seven_day":{"used_percentage":0,"resets_at":1792821600}}}""".Replace("KEY", key.Id));
        using var http = new UsageHttp(new FakeHandler(_ => new(HttpStatusCode.OK)));
        var discovery = new ConnectionDiscovery(http, _ => Environment.ProcessPath!, (_, arguments, _) =>
            Task.FromResult(arguments[0] == "--version" ? (0, "2.1.0 (Claude Code)") :
                (0, """{"loggedIn":true,"email":"sample@example.com"}""")), file);
        var probe = await discovery.ProbeAsync(Provider.Claude, @"C:\Example\.codex", default);
        Equal(DiscoveryStatus.Ready, probe.Status);
        var applied = new DesktopSettings { ClaudeMode = ConnectionMode.NeedsConfirmation }
            .CompleteDiscovery(new DesktopSettings { ClaudeMode = ConnectionMode.NeedsConfirmation }, probe, false);
        True(applied.ClaudeEnabled);
        Equal("sample@example.com", applied.ClaudeAccountLabel);
        Equal(file, applied.ClaudeSnapshot);
        applied.Validate();
        var other = new ConnectionDiscovery(http, _ => Environment.ProcessPath!, (_, arguments, _) =>
            Task.FromResult(arguments[0] == "--version" ? (0, "2.1.0") :
                (0, """{"loggedIn":true,"email":"other@example.com"}""")), file);
        Equal(DiscoveryStatus.Unsupported, (await other.ProbeAsync(Provider.Claude, @"C:\Example\.codex", default)).Status);
    }
    finally { Directory.Delete(dir, true); }
});
AsyncTest("legacy JSON migration runs once and retains manual fields", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), $"ai-usage-legacy-{Guid.NewGuid():N}");
    Directory.CreateDirectory(home);
    try
    {
        var path = Path.Combine(home, "settings.json");
        await File.WriteAllTextAsync(path, """{"GithubLogin":"sample-user","CopilotEnabled":false,"Theme":"Light"}""");
        var migrated = await DesktopSettings.LoadAsync(path, default);
        Equal(ConnectionMode.Manual, migrated.CopilotMode);
        Equal(ConnectionMode.NeedsConfirmation, migrated.CodexMode);
        await (migrated with { CodexMode = ConnectionMode.Disabled }).SaveAsync(path, default);
        var after = await DesktopSettings.LoadAsync(path, default);
        Equal(ConnectionMode.Disabled, after.CodexMode);
        Equal("sample-user", after.GithubLogin);
        Equal("Light", after.Theme);
    }
    finally { Directory.Delete(home, true); }
});
HostLifetimeTests.Register(Test);
UsageStripPresentationTests.Register(Test);
AccountTests.Register(Test);
Test("native strip renders hatch contrast localized tooltip hit-test and neutral app access", () => NativeStripRenderSmoke.Run());
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
