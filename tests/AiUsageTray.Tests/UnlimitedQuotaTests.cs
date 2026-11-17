using System.Text.Json;
using AiUsageTray.Core;
using AiUsageTray.Desktop;

/// <summary>Unlimited quotas: compact strip amounts, amount-step notifications and history of reported usage.</summary>
static class UnlimitedQuotaTests
{
    public static void Register(Action<string, Action> test)
    {
        var start = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var end = start.AddMonths(1);
        var now = DateTimeOffset.Parse("2026-11-17T12:00:00+09:00");
        void Check(bool condition, string what) { if (!condition) throw new InvalidOperationException("Unlimited quota: " + what); }
        void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"Expected {expected}; actual {actual}");
        }
        QuotaSnapshot Unlimited(string identity, string bucket, decimal? used, DateTimeOffset? observed = null) =>
            new(new(AccountKey.FromIdentity(Provider.Copilot, identity), bucket), "enterprise", UsageSource.CopilotHttp,
                observed ?? now, used is null ? UsageUnit.Unknown : UsageUnit.Credits, used, null, null, true,
                new(WindowKind.Monthly, start, end), used);
        QuotaSnapshot Limited(string identity, decimal percent) =>
            new(new(AccountKey.FromIdentity(Provider.Copilot, identity), "premium"), "enterprise", UsageSource.CopilotHttp,
                now, UsageUnit.PercentagePoints, percent, 100, percent, false, new(WindowKind.Monthly, start, end));

        test("compact amounts use k and M without carrying rounding errors", () =>
        {
            foreach (var (value, text) in new (decimal, string)[]
            {
                (0, "0"), (999, "999"), (999.96m, "1k"), (1000, "1k"), (1234, "1.2k"), (9999, "10k"), (66987, "67k"),
                (99_949, "99.9k"), (99_950, "100k"), (123_456, "123k"), (999_499, "999k"), (999_500, "1M"),
                (1_234_567, "1.2M"), (123_456_789, "123M")
            })
                Equal(text, AmountFormat.Compact(value));
            Equal("-1.2k", AmountFormat.Compact(-1234));
        });

        test("strip shows the compact amount for an account that only reports unlimited quotas", () =>
        {
            var settings = new DesktopSettings { CopilotEnabled = true, GithubLogin = "lab",
                GithubPinnedAccount = AccountKey.FromIdentity(Provider.Copilot, "lab").Id };
            var snapshots = new[] { Unlimited("lab", "chat", 100), Unlimited("lab", "premium", 66_987), Unlimited("lab", "completions", 0) };
            var value = UsageStrip.ValuesByAccount(settings.Accounts(), snapshots, now, TimeSpan.FromMinutes(10)).Single();
            Check(value.Percentage is null && value.Amount == 66_987 && value.Unlimited && value.Unit == UsageUnit.Credits, "busiest bucket amount");
            var strip = UsageStripPresentation.Create(settings, snapshots, now, TimeSpan.FromMinutes(10));
            Equal("Copilot 67k", strip.Items.Single().Text);
            Check(!strip.Items.Single().Stale, "fresh");
            Check(strip.Tooltip.Contains("66,987"), "tooltip keeps the full amount");
            var stale = UsageStripPresentation.Create(settings, snapshots, now.AddHours(1), TimeSpan.FromMinutes(10));
            Check(stale.Items.Single().Stale, "stale amount is marked stale");
        });

        test("strip keeps the percentage when the account also has a limited quota and separates accounts", () =>
        {
            var settings = new DesktopSettings { CopilotEnabled = true, GithubLogin = "lab",
                GithubPinnedAccount = AccountKey.FromIdentity(Provider.Copilot, "lab").Id };
            settings = settings.AddAccount(new AccountProfile { Id = "x1", Provider = Provider.Copilot, Login = "work",
                PinnedAccount = AccountKey.FromIdentity(Provider.Copilot, "work").Id })
                .WithPreference("x1", new() { ShowInStrip = true });
            var snapshots = new[] { Unlimited("lab", "premium", 66_987), Limited("work", 5.9m), Unlimited("work", "chat", 10) };
            var strip = UsageStripPresentation.Create(settings, snapshots, now, TimeSpan.FromMinutes(10));
            Equal("Copilot·lab 67k  |  Copilot·work 5.9%", strip.Text);
        });

        test("unlimited usage notifies at every amount step once per period and never as a percentage", () =>
        {
            var state = new UsageState();
            var first = UsageEngine.Apply(state, new FetchResult([Unlimited("lab", "premium", 12_000, now)], []), now);
            var notice = first.Single();
            Equal(NotificationReason.AmountStep, notice.Reason);
            Check(notice.CrossedLevels.SequenceEqual([5000m, 10000m]), "first observation reports the levels already reached");
            var again = UsageEngine.Apply(state, new FetchResult([Unlimited("lab", "premium", 14_999, now.AddMinutes(5))], []), now.AddMinutes(5));
            Check(again.Count == 0, "no repeat below the next step");
            var next = UsageEngine.Apply(state, new FetchResult([Unlimited("lab", "premium", 15_000, now.AddMinutes(10))], []), now.AddMinutes(10));
            Check(next.Single().CrossedLevels.SequenceEqual([15000m]), "next step fires once");
            Check(UsageEngine.Apply(state, new FetchResult([Unlimited("lab", "premium", 15_500, now.AddMinutes(15))], []), now.AddMinutes(15)).Count == 0,
                "same step is deduplicated");
        });

        test("amount step can be changed or disabled per quota and rejects invalid values", () =>
        {
            var key = Unlimited("lab", "premium", 0).Key;
            var state = new UsageState { QuotaRules = [new(key, new NotificationRules { AmountStep = 1000 })] };
            var small = UsageEngine.Apply(state, new FetchResult([Unlimited("lab", "premium", 2_500, now)], []), now).Single();
            Check(small.CrossedLevels.SequenceEqual([1000m, 2000m]), "custom step");
            var off = new UsageState { Rules = new NotificationRules { AmountStep = null } };
            Check(UsageEngine.Apply(off, new FetchResult([Unlimited("lab", "premium", 90_000, now)], []), now).Count == 0, "disabled");
            var rejected = false;
            try { new NotificationRules { AmountStep = 0 }.Validate(); } catch (UsageException) { rejected = true; }
            Check(rejected, "zero step rejected");
            Equal<decimal?>(5000, new NotificationRules().AmountStep);
        });

        test("a flood of levels is capped and unlimited quotas without a measurement stay silent", () =>
        {
            var state = new UsageState { Rules = new NotificationRules { AmountStep = 1 } };
            var notice = UsageEngine.Apply(state, new FetchResult([Unlimited("lab", "premium", 100_000, now)], []), now).Single();
            Equal((int)NotificationRules.MaximumAmountLevels, notice.CrossedLevels.Length);
            Equal(100_000m, notice.CrossedLevels[^1]);
            Check(UsageEngine.Apply(new UsageState(), new FetchResult([Unlimited("lab", "chat", null)], []), now).Count == 0, "unknown usage");
        });

        test("legacy notification rules without an amount step load with the default", () =>
        {
            var rules = JsonSerializer.Deserialize<NotificationRules>("""{"PercentageStep":10,"Thresholds":[80]}""", StateStore.JsonOptions)!;
            Equal<decimal?>(NotificationRules.DefaultAmountStep, rules.AmountStep);
            rules.Validate();
        });

        test("unlimited consumption is recorded for history in credits", () =>
        {
            var state = new UsageState();
            UsageEngine.Apply(state, new FetchResult([Unlimited("lab", "premium", 1_000, now)], []), now);
            UsageEngine.Apply(state, new FetchResult([Unlimited("lab", "premium", 1_700, now.AddMinutes(5))], []), now.AddMinutes(5));
            var delta = state.Deltas.Last();
            Equal(DeltaKind.Consumption, delta.Kind);
            Equal<decimal?>(700m, delta.Amount);
            Equal(UsageUnit.Credits, delta.Unit);
            var records = DailyHistory.Import(state);
            Check(records.Count > 0, "history import keeps the unlimited quota");
            var day = DailyHistory.Project(JapaneseCalendar.Date(now.AddMinutes(5)),
                state.Deltas.Select(d => HistoryRecord.Create(
                    state.Latest.First(s => s.Key == d.Key) with { ObservedAt = d.To }, d)), TimeSpan.FromMinutes(30)).Single();
            Equal<decimal?>(700m, day.Observed);
            Equal<decimal?>(null, day.Guidance);
        });

        test("amount-step notices start again in a new quota period", () =>
        {
            var state = new UsageState();
            UsageEngine.Apply(state, new FetchResult([Unlimited("lab", "premium", 6_000, now)], []), now);
            var nextStart = end;
            var nextEnd = end.AddMonths(1);
            var december = now.AddDays(20);
            var renewed = Unlimited("lab", "premium", 5_200, december) with { Window = new(WindowKind.Monthly, nextStart, nextEnd), CreditsUsed = 5_200 };
            var notices = UsageEngine.Apply(state, new FetchResult([renewed], []), december);
            Check(notices.Any(n => n.Reason == NotificationReason.AmountStep && n.CrossedLevels.SequenceEqual([5000m])),
                "the new period notifies its own 5,000 step");
        });

        test("settings turn an invalid amount step into the localized number message", () =>
        {
            foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
                Check(!new UiLocalizer(language).Error("settings_notification_number_invalid").Contains("Unknown"), "known code");
            var rejected = false;
            try { new NotificationRules { AmountStep = -5 }.Validate(); } catch (UsageException) { rejected = true; }
            Check(rejected, "negative step");
        });

        test("history record ids stay tied to the indented serialization (changing JsonOptions would orphan stored history)", () =>
        {
            var from = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
            var account = AccountKey.FromIdentity(Provider.Copilot, "id-stability");
            var snapshot = new QuotaSnapshot(new(account, "premium"), "enterprise", UsageSource.CopilotHttp, from.AddMinutes(5),
                UsageUnit.Credits, 7, null, null, true, new(WindowKind.Monthly, from, from.AddMonths(1)), 7);
            var delta = new UsageDelta(snapshot.Key, snapshot.PeriodId, from, from.AddMinutes(5), UsageUnit.Credits, 7, DeltaKind.Consumption);
            // Windows-only product: the indented form uses the platform newline, like every id already stored.
            Equal("B7D199DEFA9E4948108CB3697DA52F53B70E2189CF939AAC6DB39991BC27FF61", HistoryRecord.Create(snapshot, delta).Id);
            Check(StateStore.JsonOptions.WriteIndented && !StateStore.CompactFileOptions.WriteIndented, "only the file options are compact");
        });

        test("state.json is written compact, still loads, and indented files from older versions still load", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ai-usage-tray-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "state.json");
                var state = new UsageState();
                var key = Unlimited("lab", "premium", 0).Key;
                for (var i = 0; i < 300; i++)
                    state.Deltas.Add(new(key, "period", now.AddMinutes(i), now.AddMinutes(i + 5), UsageUnit.Credits, 0, DeltaKind.Consumption));
                state.Baselines.Add(Unlimited("lab", "premium", 0));
                using (var store = new StateStore(path)) store.SaveAsync(state, default).GetAwaiter().GetResult();
                var compact = File.ReadAllText(path);
                Check(!compact.Contains("\n"), "no line breaks or indentation");
                var indented = JsonSerializer.Serialize(state, StateStore.JsonOptions);
                Check(compact.Length < indented.Length * 0.7, $"compact {compact.Length} vs indented {indented.Length}");
                using (var store = new StateStore(path))
                    Equal(300, store.LoadAsync(default).GetAwaiter().GetResult().Deltas.Count);
                File.WriteAllText(path, indented);
                using (var store = new StateStore(path))
                    Equal(300, store.LoadAsync(default).GetAwaiter().GetResult().Deltas.Count);
                Check(Directory.GetFiles(folder, "*.tmp").Length == 0, "no temporary file left");
            }
            finally { Directory.Delete(folder, true); }
        });
        test("an early client disconnect is not reported as a host failure but other IO errors still are", () =>
        {
            Check(PipeProtocol.IsPeerDisconnect(new EndOfStreamException()), "end of stream");
            Check(PipeProtocol.IsPeerDisconnect(new IOException("closed", unchecked((int)0x8007006D))), "broken pipe");
            Check(PipeProtocol.IsPeerDisconnect(new IOException("closed", unchecked((int)0x800700E8))), "pipe closing");
            Check(!PipeProtocol.IsPeerDisconnect(new IOException("disk", unchecked((int)0x80070020))), "sharing violation stays an error");
            Check(!PipeProtocol.IsPeerDisconnect(new UsageException(FailureKind.Schema, "ipc_invalid_message_size")), "schema error stays");
        });

        test("amount step labels exist in both languages and the notice names the amount", () =>
        {
            foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
            {
                var locale = new UiLocalizer(language);
                Check(locale.Text(UiText.AmountStepInput).Contains("AIC"), "settings label mentions AIC");
                Check(locale.Text(UiText.UsageStripNotes).Contains("67k") && locale.Text(UiText.UsageStripNotes).Contains("cp") && locale.Text(UiText.NotificationNotes).Contains("AIC"), "help texts explain unlimited quotas");
                Check(locale.Format(UiText.NotificationAmount, "lab", "premium", 65_000m, locale.Unit(UsageUnit.Credits)).Contains("65,000"),
                    "notice shows the grouped amount");
            }
        });
    }
}
