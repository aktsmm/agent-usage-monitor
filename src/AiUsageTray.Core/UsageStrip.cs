namespace AiUsageTray.Core;

public sealed record UsageStripPosition(int X, int Y);
public sealed record StripArea(int Left, int Top, int Right, int Bottom);
public sealed record AccountStripValue(string ProfileId, Provider Provider, string Name, WindowKind Window,
    decimal? Percentage, DateTimeOffset? ObservedAt, bool Stale, bool Ambiguous,
    decimal? Amount = null, UsageUnit Unit = UsageUnit.Unknown, bool Unlimited = false);

public static class UsageStrip
{
    /// <summary>
    /// One value per account shown in the strip. Accounts of one service are never merged: each reads only the snapshots of its own
    /// account. An account whose identity is not known yet reads the service's snapshots only when it is the service's sole account.
    /// </summary>
    public static IReadOnlyList<AccountStripValue> ValuesByAccount(IEnumerable<AccountEntry> entries,
        IEnumerable<QuotaSnapshot> snapshots, DateTimeOffset now, TimeSpan maximumAge, bool showAllBuckets = false)
    {
        var all = entries.ToList();
        var list = QuotaVisibility.PrimaryBuckets(snapshots, s => s.Key, showAllBuckets).ToList();
        var visible = new List<AccountEntry>();
        var seenCopilotAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in all.Where(e => e.InStrip))
        {
            if (entry.Profile.Provider == Provider.Copilot &&
                entry.Profile.CanonicalAccountId is { } accountId &&
                !seenCopilotAccounts.Add(accountId))
                continue;
            visible.Add(entry);
        }
        return visible.Select(entry =>
        {
            var provider = entry.Profile.Provider;
            var window = provider == Provider.Copilot ? WindowKind.Monthly : WindowKind.Weekly;
            var expected = entry.Profile.ExpectedAccount;
            var soleOfService = all.Count(e => e.Profile.Provider == provider && e.Profile.Enabled) == 1;
            var matching = list.Where(s => s.Key.Account.Provider == provider && s.Window.Kind == window &&
                (expected is null ? soleOfService : string.Equals(expected, s.Key.Account.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
            var candidates = matching.Where(s => !s.Unlimited).ToArray();
            // An account that only reports unlimited quotas shows its busiest bucket's reported amount (no percentage exists).
            var unlimited = candidates.Length == 0
                ? matching.Where(s => s.HasMeasurement).OrderByDescending(s => s.Used ?? 0)
                    .ThenBy(s => QuotaVisibility.BucketPreference(s.Key))
                    .ThenBy(s => s.Key.Bucket, StringComparer.Ordinal).FirstOrDefault()
                : null;
            var snapshot = candidates.Length == 1 ? candidates[0] : unlimited;
            return new AccountStripValue(entry.Profile.Id, provider, entry.Name, window, unlimited is null ? snapshot?.UsedPercent : null,
                snapshot?.ObservedAt, snapshot is not null && !snapshot.IsFresh(now, maximumAge), candidates.Length > 1,
                unlimited?.Used, unlimited?.Unit ?? UsageUnit.Unknown, unlimited is not null);
        }).ToArray();
    }

    public static UsageStripPosition Clamp(UsageStripPosition requested, StripArea safe, int width, int height)
    {
        if (width <= 0 || height <= 0 || safe.Right <= safe.Left || safe.Bottom <= safe.Top)
            throw new UsageException(FailureKind.Schema, "usage_strip_geometry_invalid");
        return new(Math.Clamp(requested.X, safe.Left, Math.Max(safe.Left, safe.Right - width)),
            Math.Clamp(requested.Y, safe.Top, Math.Max(safe.Top, safe.Bottom - height)));
    }
}
