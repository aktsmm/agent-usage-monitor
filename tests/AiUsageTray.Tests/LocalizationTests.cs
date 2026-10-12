using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiUsageTray.Core;
using AiUsageTray.Desktop;
using JapaneseCalendar = AiUsageTray.Core.JapaneseCalendar;

static class LocalizationTests
{
    public static void Add(List<(string Name, Func<Task> Run)> tests)
    {
        void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
        void True(bool value) { if (!value) throw new InvalidOperationException("Localization assertion failed"); }
        void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"Expected {expected}; actual {actual}");
        }
        Test("ja/en resources exactly match typed keys and format placeholders", () =>
        {
            var expected = Enum.GetNames<UiText>().ToHashSet();
            foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
            {
                var actual = UiLocalizer.ResourceSet(language).Cast<DictionaryEntry>()
                    .Select(e => (string)e.Key).ToHashSet();
                True(actual.SetEquals(expected));
            }
            var ja = new UiLocalizer(DisplayLanguage.Japanese);
            var en = new UiLocalizer(DisplayLanguage.English);
            foreach (var key in Enum.GetValues<UiText>())
            {
                var a = ja.Text(key); var b = en.Text(key);
                True(a.Length > 0 && b.Length > 0);
                Equal(CompositeFormat.Parse(a).MinimumArgumentCount, CompositeFormat.Parse(b).MinimumArgumentCount);
                string[] Fields(string value) => Regex.Matches(value, @"(?<!\{)\{(\d+)([^}]*)\}")
                    .Select(m => m.Value).Order().ToArray();
                True(Fields(a).SequenceEqual(Fields(b)));
            }
        });
        Test("system language selects Japanese only for ja cultures", () =>
        {
            foreach (string culture in new[] { "ja", "ja-JP" })
                Equal(DisplayLanguage.Japanese, new UiLocalizer(DisplayLanguage.System,
                    CultureInfo.GetCultureInfo(culture)).Language);
            foreach (string culture in new[] { "en-US", "en-GB", "fr-FR", "zh-CN" })
                Equal(DisplayLanguage.English, new UiLocalizer(DisplayLanguage.System,
                    CultureInfo.GetCultureInfo(culture)).Language);
            Equal(DisplayLanguage.English, new UiLocalizer(DisplayLanguage.English,
                CultureInfo.GetCultureInfo("ja-JP")).Language);
            Equal(DisplayLanguage.Japanese, new UiLocalizer(DisplayLanguage.Japanese,
                CultureInfo.GetCultureInfo("en-US")).Language);
        });
        Test("localized errors keep diagnostic codes and reject unknown resource keys", () =>
        {
            var locale = new UiLocalizer(DisplayLanguage.English);
            var missing = locale.Error("対象の履歴ファイルはまだ作成されていません。");
            True(missing.Contains("not been created") && missing.Contains("history_file_missing"));
            True(locale.Error("synthetic_new_error_code").Contains("synthetic_new_error_code"));
            try { locale.Text((UiText)99999); }
            catch (UsageException ex) when (ex.Kind == FailureKind.Schema) { return; }
            throw new InvalidOperationException("Unknown resource key silently succeeded");
        });
        tests.Add(("manual language and explicit strip OFF persist without changing selected account", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "locale-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "settings.json");
                var before = new DesktopSettings { CopilotEnabled = true, GithubLogin = "synthetic",
                    CopilotMode = ConnectionMode.Manual, Theme = "Light", AutoUpdateEnabled = false };
                var after = before with { Language = DisplayLanguage.English, UsageStripEnabled = false };
                await after.SaveAsync(path, default);
                var loaded = await DesktopSettings.LoadAsync(path, default);
                Equal(DisplayLanguage.English, loaded.Language);
                Equal(false, loaded.UsageStripEnabled);
                Equal(false, loaded.AutoUpdateEnabled);
                Equal(before.SelectionId(Provider.Copilot), loaded.SelectionId(Provider.Copilot));
                Equal("Light", loaded.Theme);
                await File.WriteAllTextAsync(path, """{"GithubLogin":"synthetic","CopilotEnabled":true,"Theme":"Light"}""");
                var migrated = await DesktopSettings.LoadAsync(path, default);
                Equal(DisplayLanguage.System, migrated.Language);
                Equal(true, migrated.UsageStripEnabled);
                Equal(ConnectionMode.Manual, migrated.CopilotMode);
                Equal("synthetic", migrated.GithubLogin);
                await File.WriteAllTextAsync(path, """{"Version":1,"Language":"Japanese","UsageStripEnabled":false}""");
                var versionOne = await DesktopSettings.LoadAsync(path, default);
                Equal(DisplayLanguage.Japanese, versionOne.Language);
                Equal(false, versionOne.UsageStripEnabled);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }));
        Test("display language does not change period IDs record IDs budgets or notification dedup", () =>
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                var at = DateTimeOffset.Parse("2026-10-12T00:00:00+09:00", CultureInfo.InvariantCulture);
                var key = new QuotaKey(AccountKey.FromIdentity(Provider.Copilot, "synthetic"), "monthly");
                var snapshot = new QuotaSnapshot(key, "synthetic", UsageSource.CopilotHttp, at,
                    UsageUnit.PercentagePoints, 96, 100, 96, false, new(WindowKind.Monthly, at.AddDays(-11), at.AddDays(20)));
                var state = new UsageState();
                UsageEngine.Apply(state, new([snapshot], []), at);
                var before = JsonSerializer.Serialize(state, StateStore.JsonOptions);
                var record = HistoryRecord.Create(snapshot, state.Deltas.Single());
                var budget = Budgets.Calculate(snapshot);
                foreach (var name in new[] { "ja-JP", "en-US", "fr-FR" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                    var locale = new UiLocalizer(DisplayLanguage.System, CultureInfo.GetCultureInfo(name));
                    _ = locale.Format(UiText.UsedRemaining, 96m, 4m);
                    Equal(record.Id, HistoryRecord.Create(snapshot, state.Deltas.Single()).Id);
                    Equal(budget, Budgets.Calculate(snapshot));
                    Equal(before, JsonSerializer.Serialize(state, StateStore.JsonOptions));
                    Equal(at, JapaneseCalendar.DayStart(at));
                }
                var next = snapshot with { ObservedAt = at.AddMinutes(5) };
                Equal(0, UsageEngine.Apply(state, new([next], []), next.ObservedAt).Count);
            }
            finally { CultureInfo.CurrentCulture = original; }
        });
        Test("usage strip uses contract monthly/weekly percentage not guidance or short quota", () =>
        {
            var at = DateTimeOffset.UtcNow;
            QuotaSnapshot Sample(Provider provider, WindowKind kind, decimal percent) =>
                new(new(AccountKey.FromIdentity(provider, "synthetic"), kind.ToString()), "synthetic",
                    UsageSource.CopilotHttp, at, UsageUnit.PercentagePoints, percent, 100, percent, false,
                    new(kind, at.AddDays(-1), at.AddDays(kind == WindowKind.Monthly ? 30 : 6)));
            var copilot = Sample(Provider.Copilot, WindowKind.Monthly, 36);
            var codex = Sample(Provider.Codex, WindowKind.Weekly, 42);
            var shortQuota = Sample(Provider.Claude, WindowKind.Short, 99);
            var values = UsageStrip.Values([copilot, codex, shortQuota], at, TimeSpan.FromMinutes(10));
            Equal<decimal?>(36, values[0].Percentage);
            Equal<decimal?>(42, values[1].Percentage);
            Equal<decimal?>(null, values[2].Percentage);
            True(UsageStrip.Values([copilot], at.AddHours(1), TimeSpan.FromMinutes(10))[0].Stale);
            var other = copilot with { Key = new(copilot.Key.Account, "other quota") };
            var ambiguous = UsageStrip.Values([copilot, other], at, TimeSpan.FromMinutes(10))[0];
            True(ambiguous.Ambiguous && ambiguous.Percentage is null);
        });
        Test("usage strip geometry clamps saved off-screen coordinates inside work area", () =>
        {
            Equal(new UsageStripPosition(100, 200), UsageStrip.Clamp(new(-500, -500), new(100, 200, 900, 800), 300, 32));
            Equal(new UsageStripPosition(600, 768), UsageStrip.Clamp(new(9999, 9999), new(100, 200, 900, 800), 300, 32));
            Equal(new UsageStripPosition(-1400, 100), UsageStrip.Clamp(new(-1400, 100), new(-1920, 0, 0, 1000), 300, 32));
        });
        Test("native strip shows without activation and releases GDI objects after repeated toggles", NativeStripSmoke.Run);
    }
}
