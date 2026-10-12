using System;
using System.Collections.Generic;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public static class DemoView
{
    public static DesktopView Create(bool light)
    {
        var now = DateTimeOffset.UtcNow;
        var monthEnd = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var copilot = new QuotaSnapshot(new(AccountKey.FromIdentity(Provider.Copilot, "demo-only"), "月枠"),
            "サンプル", UsageSource.CopilotHttp, now, UsageUnit.PercentagePoints, 32, 100, 32, false,
            new(WindowKind.Monthly, monthEnd.AddMonths(-1), monthEnd));
        var codex = new QuotaSnapshot(new(AccountKey.FromIdentity(Provider.Codex, "demo-only"), "週枠"),
            "サンプル", UsageSource.CodexHttp, now, UsageUnit.PercentagePoints, 24, 100, 24, false,
            new(WindowKind.Weekly, now.AddDays(-3), now.AddDays(4)));
        var state = new UsageState { Latest = [copilot, codex] };
        var views = new List<QuotaView>();
        foreach (var snapshot in state.Latest)
        {
            var day = Budgets.Summarize(state, snapshot, false) with { Measured = snapshot == codex ? 16 : 0.9m, Coverage = Coverage.Partial };
            var week = Budgets.Summarize(state, snapshot, true) with { Measured = 6.8m, Coverage = Coverage.Partial };
            views.Add(new(snapshot.Key, day, week));
        }
        return new(state, new DesktopSettings { Theme = light ? "Light" : "Dark", CodexHome = @"C:\Example\.codex",
            ClaudeMode = ConnectionMode.Attempted, ConnectionResults = new()
            { [Provider.Claude] = new(Provider.Claude, Diagnostics.DiscoveryStatus.LoginRequired, "claude_login_required") } },
            new(), null, views, false);
    }
}
