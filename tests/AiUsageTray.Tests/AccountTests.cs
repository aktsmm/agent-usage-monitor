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
            var s = baseSettings.AddAccount(Extra("x1", "carol")).AddAccount(Extra("x2", "dave"))
                .WithPreference("x1", new() { Alias = "alice-work" })
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
        test("Copilot profiles deduplicate by verified GitHub user id across CLI and VS Code", () =>
        {
            const string userId = "123456";
            var userKey = AccountKey.FromIdentity(Provider.Copilot, "github.com:" + userId).Id;
            var cli = new AccountProfile
            {
                Id = "cli",
                Provider = Provider.Copilot,
                Login = "old-login",
                PinnedAccount = userKey
            };
            var vscode = new AccountProfile
            {
                Id = "vscode",
                Provider = Provider.Copilot,
                Login = "new-login",
                VscodeUserId = userId
            };
            Check(cli.IsSameAccountAs(vscode), "same numeric GitHub id");
            Check(!cli.IsSameAccountAs(vscode with { VscodeUserId = "654321" }), "different numeric GitHub id");
            Check(!cli.IsSameAccountAs(vscode with { Login = cli.Login, VscodeUserId = "654321" }),
                "verified ids take precedence over a stale/reused login");
            var invalid = false;
            try
            {
                (vscode with
                {
                    PinnedAccount = AccountKey.FromIdentity(Provider.Copilot, "github.com:654321").Id
                }).Validate();
            }
            catch (UsageException) { invalid = true; }
            Check(invalid, "VS Code id and pinned id must agree");

            var settings = new DesktopSettings
            {
                CopilotEnabled = true,
                GithubLogin = cli.Login,
                GithubPinnedAccount = userKey
            };
            var rejected = false;
            try { settings.AddAccount(vscode); } catch (UsageException) { rejected = true; }
            Check(rejected, "CLI primary and VS Code profile");

            var vscodeSettings = new DesktopSettings { ExtraAccounts = [vscode] };
            rejected = false;
            try { vscodeSettings.AddAccount(cli with { Id = "another-cli-profile" }); }
            catch (UsageException) { rejected = true; }
            Check(rejected, "VS Code and CLI profiles");

            var duplicateSettings = new DesktopSettings
            {
                CopilotEnabled = true,
                GithubLogin = cli.Login,
                GithubPinnedAccount = userKey,
                ExtraAccounts = [vscode with { PinnedAccount = userKey }]
            }.WithPreference("vscode", new() { ShowInStrip = true });
            var values = UsageStrip.ValuesByAccount(duplicateSettings.Accounts(),
                [Snap(Provider.Copilot, "github.com:" + userId, 42)], now, TimeSpan.FromMinutes(10));
            Check(values.Count == 1 && values[0].Percentage == 42, "same account renders once in strip");

            var tampered = new DesktopSettings
            {
                CopilotEnabled = true,
                GithubLogin = "alice-work",
                ExtraAccounts = [Extra("x1", "alice-work")]
            };
            rejected = false;
            try { tampered.Validate(); } catch (UsageException) { rejected = true; }
            Check(rejected, "a hand-edited settings file cannot hold the same account twice");
        });
        test("account with only unlimited buckets stays visible next to a limited account", () =>
        {
            var limited = Snap(Provider.Copilot, "alice-work", 40);
            var unlimited = Snap(Provider.Copilot, "carol", 0) with { Unlimited = true };
            var mixedLimited = Snap(Provider.Copilot, "alice-work", 0) with { Unlimited = true };
            var shown = QuotaVisibility.HideUnlimitedPerAccount([limited, mixedLimited, unlimited]).ToArray();
            Check(shown.Contains(limited) && shown.Contains(unlimited), "both accounts visible");
            Check(!shown.Contains(mixedLimited), "unlimited bucket hidden inside a limited account");
        });
        test("overview: a primary mismatch hides only the primary's values, never another account's", () =>
        {
            var s = baseSettings.AddAccount(Extra("x1", "carol"));
            var shown = s.Accounts().ToArray();
            var snaps = new[] { Snap(Provider.Copilot, "alice-work", 40), Snap(Provider.Copilot, "carol", 70) };
            var hidden = AccountOverview.Snapshots(shown, snaps, primaryAccountMismatch: true);
            Check(hidden.Count == 1 && hidden[0].UsedPercent == 70, "extra survives primary mismatch");
            var normal = AccountOverview.Snapshots(shown, snaps, primaryAccountMismatch: false);
            Check(normal.Count == 2, "both shown without mismatch");
            var single = AccountOverview.Snapshots(baseSettings.Accounts().ToArray(), [snaps[0]], primaryAccountMismatch: true);
            Check(single.Count == 0, "single account is still emptied by its own mismatch");
        });
        test("overview: accounts without data are listed, not silently absent", () =>
        {
            var s = baseSettings.AddAccount(Extra("x1", "carol"));
            var shown = s.Accounts().ToArray();
            var missing = AccountOverview.WithoutData(shown, [Snap(Provider.Copilot, "alice-work", 40)]);
            Check(missing.Count == 1 && missing[0].Profile.Id == "x1", "carol listed");
            Check(AccountOverview.WithoutData(baseSettings.Accounts().ToArray(), []).Count == 0, "single account uses the service message");
        });
        test("notices name the account when a service has several, and stay unchanged otherwise", () =>
        {
            var s = baseSettings.AddAccount(Extra("x1", "carol")).WithPreference("x1", new() { Alias = "Home" });
            var carol = new AccountKey(Provider.Copilot, AccountKey.FromIdentity(Provider.Copilot, "carol").Id);
            Check(AccountOverview.NoticeSubject(s.Accounts().ToArray(), carol, "Copilot") == "Copilot · Home", "named");
            Check(AccountOverview.NoticeSubject(baseSettings.Accounts().ToArray(), carol, "Copilot") == "Copilot", "single unchanged");
            Check(AccountOverview.NameOf(s.Accounts().ToArray(), carol) == "Home", "history name");
        });
        test("pinning the primary ignores values that belong to additional accounts", () =>
        {
            var carolKey = AccountKey.FromIdentity(Provider.Copilot, "carol").Id;
            var s = new DesktopSettings { CopilotEnabled = true, CopilotMode = ConnectionMode.Manual, GithubLogin = "alice-work" }
                .AddAccount(Extra("x1", "carol"));
            var state = new UsageState { Latest = [Snap(Provider.Copilot, "carol", 70)] };
            Check(s.PinExisting(state).GithubPinnedAccount is null, "primary must not be pinned to carol");
            Check(carolKey.Length == 64, "sanity");
        });
        test("account strip: a primary failure does not mark another account as failed", () =>
        {
            var s = baseSettings.AddAccount(Extra("x1", "carol")).WithPreference("x1", new() { ShowInStrip = true });
            var strip = UsageStripPresentation.Create(s,
                [Snap(Provider.Copilot, "alice-work", 40), Snap(Provider.Copilot, "carol", 70)],
                now, TimeSpan.FromMinutes(10), [Provider.Copilot]);
            Check(strip.Items.Count == 2 && strip.Items[0].Stale && !strip.Items[1].Stale, "only the primary is marked");
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
            var hiddenPrimary = baseSettings.WithPreference(AccountNames.PrimaryId(Provider.Copilot),
                new() { Visible = true, ShowInStrip = false });
            var hidden = UsageStripPresentation.Create(hiddenPrimary, [Snap(Provider.Copilot, "alice-work", 40)],
                now, TimeSpan.FromMinutes(10));
            Check(!hidden.Text.Contains("40%"), "primary strip opt-out is honored");
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
