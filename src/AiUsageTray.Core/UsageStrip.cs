namespace AiUsageTray.Core;

public sealed record UsageStripPosition(int X, int Y);
public sealed record StripArea(int Left, int Top, int Right, int Bottom);
public sealed record UsageStripValue(Provider Provider, WindowKind Window, decimal? Percentage,
    DateTimeOffset? ObservedAt, bool Stale, bool Ambiguous);

public sealed record AccountStripValue(string ProfileId, Provider Provider, string Name, WindowKind Window,
    decimal? Percentage, DateTimeOffset? ObservedAt, bool Stale, bool Ambiguous);

public static class UsageStrip
{
    /// <summary>
    /// One value per account shown in the strip. Accounts of one service are never merged: each reads only the snapshots of its own
    /// account. An account whose identity is not known yet reads the service's snapshots only when it is the service's sole account.
    /// </summary>
    public static IReadOnlyList<AccountStripValue> ValuesByAccount(IEnumerable<AccountEntry> entries,
        IEnumerable<QuotaSnapshot> snapshots, DateTimeOffset now, TimeSpan maximumAge)
    {
        var all = entries.ToList();
        var list = snapshots.ToList();
        return all.Where(e => e.InStrip).Select(entry =>
        {
            var provider = entry.Profile.Provider;
            var window = provider == Provider.Copilot ? WindowKind.Monthly : WindowKind.Weekly;
            var expected = entry.Profile.ExpectedAccount;
            var soleOfService = all.Count(e => e.Profile.Provider == provider && e.Profile.Enabled) == 1;
            var candidates = list.Where(s => s.Key.Account.Provider == provider && s.Window.Kind == window && !s.Unlimited &&
                (expected is null ? soleOfService : string.Equals(expected, s.Key.Account.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
            var snapshot = candidates.Length == 1 ? candidates[0] : null;
            return new AccountStripValue(entry.Profile.Id, provider, entry.Name, window, snapshot?.UsedPercent, snapshot?.ObservedAt,
                snapshot is not null && !snapshot.IsFresh(now, maximumAge), candidates.Length > 1);
        }).ToArray();
    }

    public static IReadOnlyList<UsageStripValue> Values(IEnumerable<QuotaSnapshot> snapshots,
        DateTimeOffset now, TimeSpan maximumAge) => Enum.GetValues<Provider>().Select(provider =>
    {
        var window = provider == Provider.Copilot ? WindowKind.Monthly : WindowKind.Weekly;
        var candidates = snapshots.Where(s => s.Key.Account.Provider == provider &&
            s.Window.Kind == window && !s.Unlimited).ToArray();
        var snapshot = candidates.Length == 1 ? candidates[0] : null;
        return new UsageStripValue(provider, window, snapshot?.UsedPercent, snapshot?.ObservedAt,
            snapshot is not null && !snapshot.IsFresh(now, maximumAge), candidates.Length > 1);
    }).ToArray();

    public static UsageStripPosition Clamp(UsageStripPosition requested, StripArea safe, int width, int height)
    {
        if (width <= 0 || height <= 0 || safe.Right <= safe.Left || safe.Bottom <= safe.Top)
            throw new UsageException(FailureKind.Schema, "usage_strip_geometry_invalid");
        return new(Math.Clamp(requested.X, safe.Left, Math.Max(safe.Left, safe.Right - width)),
            Math.Clamp(requested.Y, safe.Top, Math.Max(safe.Top, safe.Bottom - height)));
    }
}
