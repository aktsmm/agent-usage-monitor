namespace AiUsageTray.Core;

public static class QuotaVisibility
{
    /// <summary>The Copilot bucket people mean by "usage". Its API key predates the current billing wording and is kept as the identifier.</summary>
    public const string CopilotPrimaryBucket = "premium_interactions";

    /// <summary>
    /// Copilot reports chat and completions next to the premium bucket. By default only the premium bucket is shown; an
    /// account without a premium bucket keeps whatever it reports so it never disappears. Other services are unchanged.
    /// This only filters what is displayed: collection, history records and notification rules are not affected.
    /// </summary>
    public static IEnumerable<T> PrimaryBuckets<T>(IEnumerable<T> items, Func<T, QuotaKey> key, bool showAllBuckets)
    {
        var list = items.ToList();
        if (showAllBuckets) return list;
        var withPrimary = list.Select(key)
            .Where(k => k.Account.Provider == Provider.Copilot && k.Bucket == CopilotPrimaryBucket)
            .Select(k => k.Account).ToHashSet();
        return list.Where(item =>
        {
            var k = key(item);
            return k.Account.Provider != Provider.Copilot || k.Bucket == CopilotPrimaryBucket ||
                !withPrimary.Contains(k.Account);
        }).ToList();
    }

    /// <summary>Orders buckets so that the Copilot premium bucket wins ties before the alphabetical fallback.</summary>
    public static int BucketPreference(QuotaKey key) =>
        key.Account.Provider == Provider.Copilot && key.Bucket == CopilotPrimaryBucket ? 0 : 1;

    /// <summary>
    /// Inside one account, unlimited buckets are hidden when the same account also has a limited one.
    /// An account that reports only unlimited buckets stays visible as a single entry (the busiest bucket, premium first on ties),
    /// even when other accounts have limited buckets; its buckets would otherwise render as identical "unlimited" cards.
    /// </summary>
    public static IEnumerable<QuotaSnapshot> HideUnlimitedPerAccount(IEnumerable<QuotaSnapshot> snapshots) =>
        snapshots.GroupBy(s => s.Key.Account.Id)
            .SelectMany(group => group.Any(s => !s.Unlimited)
                ? group.Where(s => !s.Unlimited)
                : group.OrderByDescending(s => s.Used ?? 0).ThenBy(s => BucketPreference(s.Key))
                    .ThenBy(s => s.Key.Bucket, StringComparer.Ordinal).Take(1));
}
