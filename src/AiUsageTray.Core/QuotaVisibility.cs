namespace AiUsageTray.Core;

public static class QuotaVisibility
{
    /// <summary>
    /// Inside one account, unlimited buckets are hidden only when the same account also has a limited one.
    /// An account that reports only unlimited buckets stays visible, even when other accounts have limited buckets.
    /// </summary>
    public static IEnumerable<QuotaSnapshot> HideUnlimitedPerAccount(IEnumerable<QuotaSnapshot> snapshots) =>
        snapshots.GroupBy(s => s.Key.Account.Id)
            .SelectMany(group => group.Any(s => !s.Unlimited) ? group.Where(s => !s.Unlimited) : group);
}
