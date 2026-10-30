namespace AiUsageTray.Core;

public static class QuotaVisibility
{
    /// <summary>
    /// Inside one account, unlimited buckets are hidden when the same account also has a limited one.
    /// An account that reports only unlimited buckets stays visible as a single entry (the busiest bucket), even when other
    /// accounts have limited buckets; its buckets would otherwise render as identical "unlimited" cards.
    /// </summary>
    public static IEnumerable<QuotaSnapshot> HideUnlimitedPerAccount(IEnumerable<QuotaSnapshot> snapshots) =>
        snapshots.GroupBy(s => s.Key.Account.Id)
            .SelectMany(group => group.Any(s => !s.Unlimited)
                ? group.Where(s => !s.Unlimited)
                : group.OrderByDescending(s => s.Used ?? 0).ThenBy(s => s.Key.Bucket, StringComparer.Ordinal).Take(1));
}
