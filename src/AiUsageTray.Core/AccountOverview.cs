namespace AiUsageTray.Core;

/// <summary>
/// Per-account decisions for views and notices. Each rule works on one account at a time, so a failure, an unlimited plan or
/// a missing value of one account never changes what another account of the same service shows.
/// </summary>
public static class AccountOverview
{
    /// <summary>Snapshots to draw for one service. A mismatch of the primary account hides only the primary account's values.</summary>
    public static IReadOnlyList<QuotaSnapshot> Snapshots(IReadOnlyList<AccountEntry> shown,
        IEnumerable<QuotaSnapshot> providerSnapshots, bool primaryAccountMismatch, bool showAllBuckets = false)
    {
        if (shown.Count == 0) return [];
        var list = providerSnapshots.ToList();
        if (shown.Count > 1)
        {
            var ids = shown.Select(a => a.Profile.ExpectedAccount).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            list = list.Where(s => ids.Contains(s.Key.Account.Id)).ToList();
        }
        if (primaryAccountMismatch && shown.FirstOrDefault(a => a.IsPrimary) is { } primary)
        {
            if (primary.Profile.ExpectedAccount is { } primaryId)
                list = list.Where(s => !string.Equals(s.Key.Account.Id, primaryId, StringComparison.OrdinalIgnoreCase)).ToList();
            else if (shown.Count == 1)
                list = [];
        }
        var visible = QuotaVisibility.PrimaryBuckets(list, s => s.Key, showAllBuckets).ToList();
        // Showing every Copilot bucket is an explicit request, so unlimited ones are not collapsed away.
        if (showAllBuckets)
            return visible.Where(s => s.Key.Account.Provider == Provider.Copilot)
                .Concat(QuotaVisibility.HideUnlimitedPerAccount(visible.Where(s => s.Key.Account.Provider != Provider.Copilot))).ToList();
        return QuotaVisibility.HideUnlimitedPerAccount(visible).ToList();
    }

    /// <summary>Accounts that are shown but have no value yet. They must say so instead of silently disappearing.</summary>
    public static IReadOnlyList<AccountEntry> WithoutData(IReadOnlyList<AccountEntry> shown, IEnumerable<QuotaSnapshot> snapshots)
    {
        if (shown.Count < 2) return [];
        var have = snapshots.Select(s => s.Key.Account.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return shown.Where(a => a.Profile.ExpectedAccount is not { } id || !have.Contains(id)).ToList();
    }

    public static string? NameOf(IEnumerable<AccountEntry> entries, AccountKey key) =>
        entries.FirstOrDefault(e => e.Profile.Provider == key.Provider &&
            string.Equals(e.Profile.ExpectedAccount, key.Id, StringComparison.OrdinalIgnoreCase))?.Name;

    public static IEnumerable<string> AmountWarnings(
        UsageState state, Provider provider, UiLocalizer locale, IReadOnlyList<AccountEntry> accounts)
    {
        var hidden = false;
        foreach (var snapshot in state.Latest.Where(s => s.Key.Account.Provider == provider))
            if (locale.AmountNotificationWarning(state, snapshot) is { } warning)
            {
                var account = accounts.FirstOrDefault(a => a.Profile.Provider == provider &&
                    a.Profile.Enabled && a.Preference.Visible &&
                    string.Equals(a.Profile.ExpectedAccount, snapshot.Key.Account.Id, StringComparison.OrdinalIgnoreCase));
                if (account is null) hidden = true;
                else yield return account.Name + " · " + warning;
            }
        if (hidden) yield return locale.Text(UiText.AmountHiddenNotificationPrecisionWarning);
    }

    /// <summary>Notice subject: the service name, plus the account name when the service has several enabled accounts.</summary>
    public static string NoticeSubject(IReadOnlyList<AccountEntry> entries, AccountKey key, string providerName)
    {
        var several = entries.Count(e => e.Profile.Provider == key.Provider && e.Profile.Enabled) > 1;
        return several && NameOf(entries, key) is { } name ? providerName + " · " + name : providerName;
    }
}
