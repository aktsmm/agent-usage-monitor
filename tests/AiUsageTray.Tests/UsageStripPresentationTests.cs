using AiUsageTray.Core;
using AiUsageTray.Desktop;

static class UsageStripPresentationTests
{
    public static void Register(Action<string, Action> test)
    {
        var now = DateTimeOffset.Parse("2026-10-13T00:00:00+09:00");
        var age = TimeSpan.FromMinutes(10);
        var copilot = Sample(Provider.Copilot, 0, now);
        var codex = Sample(Provider.Codex, 84, now);
        var settings = new DesktopSettings { CopilotEnabled = true, GithubLogin = "synthetic",
            GithubPinnedAccount = copilot.Key.Account.Id, CodexEnabled = true,
            CodexPinnedAccount = codex.Key.Account.Id, CodexEmail = "synthetic@example.test",
            ClaudeEnabled = true, ClaudeAccountLabel = "synthetic",
            Language = DisplayLanguage.English };
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Strip presentation mismatch"); }

        test("strip hides missing services but preserves a measured zero and separators", () =>
        {
            var view = UsageStripPresentation.Create(settings, [copilot, codex], now, age);
            Check(view.Text == "Copilot 0%  |  Codex 84%");
            Check(view.Items.Count == 2 && view.Items.All(item => !item.Stale));
            Check(!view.Text.Contains("Claude") && view.Tooltip.Contains("Claude · Waiting for usage"));
        });
        test("strip null percentages and non-contract windows are hidden not zero", () =>
        {
            var unknown = copilot with { UsedPercent = null };
            var shortQuota = Sample(Provider.Claude, 99, now) with
                { Window = new(WindowKind.Short, now.AddHours(-1), now.AddHours(1)) };
            var view = UsageStripPresentation.Create(settings, [unknown, shortQuota], now, age);
            Check(view.Text == "Waiting for usage" && view.Items.Count == 1 && !view.Items[0].Stale);
            Check(!view.Text.Contains('%') && !view.Text.Contains('|'));
        });
        test("strip stale values retain percentages without long labels and tooltip keeps time", () =>
        {
            var old = codex with { ObservedAt = now.AddMinutes(-11) };
            var view = UsageStripPresentation.Create(settings, [copilot, old], now, age);
            Check(view.Text == "Copilot 0%  |  Codex 84%");
            Check(!view.Items[0].Stale && view.Items[1].Stale);
            Check(!view.Text.Contains("Stale") && view.Tooltip.Contains("Codex 84% · Stale"));
            Check(view.Tooltip.Contains("2026-10-12 23:49:00 JST"));
        });
        test("strip provider failure marks even recent zero as stale never fresh", () =>
        {
            var view = UsageStripPresentation.Create(settings, [copilot], now, age, [Provider.Copilot, Provider.Claude]);
            Check(view.Items[0].Text == "Copilot 0%" && view.Items[0].Stale);
            Check(view.Tooltip.Contains("Copilot 0% · Fetch failed"));
            Check(view.Tooltip.Contains("Claude · Fetch failed"));
        });
        test("strip freshness retains exact age boundary and respects reset and future observations", () =>
        {
            var boundary = codex with { ObservedAt = now - age };
            Check(!UsageStripPresentation.Create(settings, [boundary], now, age).Items[0].Stale);
            Check(UsageStripPresentation.Create(settings, [boundary], now.AddTicks(1), age).Items[0].Stale);
            var reset = codex with { Window = codex.Window with { ResetsAt = now } };
            Check(UsageStripPresentation.Create(settings, [reset], now, age).Items[0].Stale);
            Check(UsageStripPresentation.Create(settings, [codex with { ObservedAt = now.AddSeconds(1) }],
                now, age).Items[0].Stale);
        });
        test("strip ambiguous contract quotas never choose one and render no empty separator", () =>
        {
            var second = copilot with { Key = new(copilot.Key.Account, "second") };
            var view = UsageStripPresentation.Create(settings, [copilot, second, codex], now, age);
            Check(view.Text == "Codex 84%" && !view.Text.Contains('|'));
            Check(view.Tooltip.Contains(new UiLocalizer(DisplayLanguage.English).Text(UiText.UsageStripAmbiguous)));
        });
        test("strip disabled provider and changed pinned account cannot expose old values", () =>
        {
            var disabled = settings with { CopilotEnabled = false };
            Check(UsageStripPresentation.Create(disabled, [copilot, codex], now, age).Text == "Codex 84%");
            var changed = settings with { GithubPinnedAccount = AccountKey.FromIdentity(Provider.Copilot, "different").Id };
            Check(UsageStripPresentation.Create(changed, [copilot, codex], now, age).Text == "Codex 84%");
            var claude = Sample(Provider.Claude, 42, now);
            Check(UsageStripPresentation.Create(settings, [claude], now, age).Text == "Claude 42%");
            Check(UsageStripPresentation.Create(settings with { ClaudeAccountLabel = "different" }, [claude],
                now, age).Text == "Waiting for usage");
        });
        test("strip waiting state and hover status localize without altering settings or snapshots", () =>
        {
            var ja = settings with { Language = DisplayLanguage.Japanese };
            Check(UsageStripPresentation.Create(ja, [], now, age).Text == "取得待ち");
            var view = UsageStripPresentation.Create(ja, [copilot], now, age);
            Check(view.Text == "Copilot 0%" && view.Tooltip.Contains("最終取得: 2026-10-13 00:00:00 JST"));
            Check(view.Tooltip.Contains("Claude · 取得待ち"));
            var jaNotes = new UiLocalizer(DisplayLanguage.Japanese).Text(UiText.UsageStripNotes);
            var enNotes = new UiLocalizer(DisplayLanguage.English).Text(UiText.UsageStripNotes);
            Check(jaNotes.Contains("0%") && jaNotes.Contains("取得待ち") && view.Tooltip.Contains(jaNotes));
            Check(enNotes.Contains("0%") && enNotes.Contains("Waiting"));
            Check(copilot.UsedPercent == 0 && settings.ClaudeEnabled);
        });
    }

    public static QuotaSnapshot Sample(Provider provider, decimal percentage, DateTimeOffset now) =>
        new(new(AccountKey.FromIdentity(provider, "synthetic"), "synthetic"), "Synthetic",
            UsageSource.CopilotHttp, now, UsageUnit.PercentagePoints, percentage, 100, percentage, false,
            new(provider == Provider.Copilot ? WindowKind.Monthly : WindowKind.Weekly,
                now.AddDays(-1), now.AddDays(6)));
}
