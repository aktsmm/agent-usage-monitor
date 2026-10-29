using System.Text.Json;
using AiUsageTray.Core;
using AiUsageTray.Desktop;

static class AccountTests
{
    public static void Register(Action<string, Action> test)
    {
        var now = DateTimeOffset.Parse("2026-10-13T00:00:00+09:00");
        void Check(bool condition, string what) { if (!condition) throw new InvalidOperationException("Account mismatch: " + what); }
        QuotaSnapshot Snap(Provider provider, string identity, decimal percent) =>
            UsageStripPresentationTests.Sample(provider, percent, now) with
            { Key = new(AccountKey.FromIdentity(provider, identity), "synthetic") };
        AccountProfile Extra(string id, string login) => new() { Id = id, Provider = Provider.Copilot, Login = login,
            PinnedAccount = AccountKey.FromIdentity(Provider.Copilot, login).Id };
        var baseSettings = new DesktopSettings { CopilotEnabled = true, GithubLogin = "alice-work",
            GithubPinnedAccount = AccountKey.FromIdentity(Provider.Copilot, "alice-work").Id };

        test("account auto names come from the identity without the mail domain", () =>
        {
            var codex = new AccountProfile { Provider = Provider.Codex, Email = "bob@example.test", Home = "C:\\x" };
            Check(AccountNames.Auto(codex) == "bob", "email local part");
            Check(AccountNames.Auto(codex with { Email = "", Home = "C:\\homes\\team-a\\" }) == "team-a", "home folder");
            Check(AccountNames.Shorten(new string('a', 40)).Length == AccountNames.MaximumAlias, "shortened");
        });
        test("account aliases win, collisions get stable suffixes, control characters are rejected", () =>
        {
            var s = baseSettings.AddAccount(Extra("x1", "alice-work")).AddAccount(Extra("x2", "carol"))
                .WithPreference("x2", new() { Alias = "  Personal " });
            var names = s.Accounts().Select(e => e.Name).ToArray();
            Check(names.SequenceEqual(["alice-work #1", "alice-work #2", "Personal"]), string.Join("|", names));
            var rejected = false;
            try { s.WithPreference("x1", new() { Alias = "bad\nname" }); } catch (UsageException) { rejected = true; }
            Check(rejected, "control char alias");
        });
        test("account legacy settings become one primary profile per service and strip defaults to the primary only", () =>
        {
            var s = baseSettings.AddAccount(Extra("x1", "carol"));
            var entries = s.Accounts();
            Check(entries.Count == 2 && entries[0].IsPrimary && !entries[1].IsPrimary, "primary first");
            Check(entries[0].InStrip && !entries[1].InStrip, "pinned one only");
            Check(s.WithPreference("x1", new() { ShowInStrip = true }).Accounts().Count(e => e.InStrip) == 2, "extra can be shown");
            Check(!s.WithPreference("x1", new() { ShowInStrip = true, Visible = false }).Accounts()[1].InStrip, "hidden never shows");
        });
        test("account settings round trip keeps extras and an old file without them still loads", () =>
        {
            var s = baseSettings.AddAccount(Extra("x1", "carol")).WithPreference("x1", new() { Alias = "Home", ShowInStrip = true });
            var path = Path.Combine(Path.GetTempPath(), "aiut-accounts-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                s.SaveAsync(path, default).GetAwaiter().GetResult();
                var loaded = DesktopSettings.LoadAsync(path, default).GetAwaiter().GetResult();
                Check(loaded.ExtraAccounts.Count == 1 && loaded.AccountPrefs["x1"].Alias == "Home", "round trip");
                var old = JsonSerializer.Serialize(baseSettings, StateStore.JsonOptions);
                var node = System.Text.Json.Nodes.JsonNode.Parse(old)!.AsObject();
                node.Remove("ExtraAccounts"); node.Remove("extraAccounts"); node.Remove("AccountPrefs"); node.Remove("accountPrefs");
                File.WriteAllText(path, node.ToJsonString());
                Check(DesktopSettings.LoadAsync(path, default).GetAwaiter().GetResult().ExtraAccounts.Count == 0, "old file");
            }
            finally { File.Delete(path); }
        });
        test("account duplicates and primary ids cannot be added", () =>
        {
            var s = baseSettings.AddAccount(Extra("x1", "carol"));
            foreach (var bad in new[] { Extra("x1", "dave"), Extra("primary-copilot", "dave") })
            {
                var rejected = false;
                try { s.AddAccount(bad); } catch (UsageException) { rejected = true; }
                Check(rejected, bad.Id);
            }
            Check(s.RemoveAccount("x1").ExtraAccounts.Count == 0, "remove");
        });
        test("account strip shows two accounts of one service side by side and never sums them", () =>
        {
            var s = baseSettings.AddAccount(Extra("x1", "carol")).WithPreference("x1", new() { ShowInStrip = true });
            var values = UsageStrip.ValuesByAccount(s.Accounts(),
                [Snap(Provider.Copilot, "alice-work", 40), Snap(Provider.Copilot, "carol", 70)], now, TimeSpan.FromMinutes(10));
            Check(values.Count == 2 && values[0].Percentage == 40 && values[1].Percentage == 70, "separate values");
        });
        test("account strip of a missing account stays empty while the other keeps its value", () =>
        {
            var s = baseSettings.AddAccount(Extra("x1", "carol")).WithPreference("x1", new() { ShowInStrip = true });
            var values = UsageStrip.ValuesByAccount(s.Accounts(), [Snap(Provider.Copilot, "carol", 70)], now, TimeSpan.FromMinutes(10));
            Check(values[0].Percentage is null && values[1].Percentage == 70, "isolated");
        });
        test("account strip text names accounts only when a service has several and single-account text is unchanged", () =>
        {
            var one = UsageStripPresentation.Create(baseSettings, [Snap(Provider.Copilot, "alice-work", 40)], now, TimeSpan.FromMinutes(10));
            Check(one.Text == "Copilot 40%", one.Text);
            var s = baseSettings.AddAccount(Extra("x1", "carol")).WithPreference("x1", new() { Alias = "Home", ShowInStrip = true });
            var two = UsageStripPresentation.Create(s, [Snap(Provider.Copilot, "alice-work", 40), Snap(Provider.Copilot, "carol", 70)],
                now, TimeSpan.FromMinutes(10));
            Check(two.Text == "Copilot·alice-work 40%  |  Copilot·Home 70%", two.Text);
        });
        test("account slot binding change drops only that account's cached values", () =>
        {
            var state = new UsageState { Latest = [Snap(Provider.Copilot, "alice-work", 40), Snap(Provider.Copilot, "carol", 70)] };
            state.BindSlot("x1", "sel-a", AccountKey.FromIdentity(Provider.Copilot, "carol").Id);
            state.BindSlot("x1", "sel-b", AccountKey.FromIdentity(Provider.Copilot, "carol").Id);
            Check(state.Latest.Count == 1 && state.Latest[0].UsedPercent == 40, "only carol dropped");
            state.PruneSlots(new HashSet<string>(), new Dictionary<string, string?> { ["x1"] = null });
            Check(state.SlotBindings.Count == 0, "pruned");
        });
    }
}
