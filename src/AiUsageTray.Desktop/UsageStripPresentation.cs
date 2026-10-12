using System;
using System.Collections.Generic;
using System.Linq;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public sealed record UsageStripItem(string Text, bool Stale);
public sealed record UsageStripPresentation(IReadOnlyList<UsageStripItem> Items, string Text, string Tooltip)
{
    // Input is the host's verified selection, not all persisted snapshots.
    public static UsageStripPresentation Create(DesktopSettings settings, IEnumerable<QuotaSnapshot> verified,
        DateTimeOffset now, TimeSpan maximumAge, IEnumerable<Provider>? failedProviders = null)
    {
        var locale = new UiLocalizer(settings.Language);
        bool Enabled(Provider provider) => provider switch
        {
            Provider.Copilot => settings.CopilotEnabled,
            Provider.Codex => settings.CodexEnabled,
            _ => settings.ClaudeEnabled
        };
        bool Selected(QuotaSnapshot snapshot)
        {
            var provider = snapshot.Key.Account.Provider;
            var expected = provider == Provider.Claude
                ? AccountKey.FromIdentity(provider, settings.ClaudeAccountLabel).Id
                : settings.PinnedAccount(provider);
            return Enabled(provider) && (expected is null ||
                string.Equals(expected, snapshot.Key.Account.Id, StringComparison.OrdinalIgnoreCase));
        }
        var failed = failedProviders?.ToHashSet() ?? [];
        var values = UsageStrip.Values(verified.Where(Selected), now, maximumAge).Where(v => Enabled(v.Provider));
        var items = new List<UsageStripItem>();
        var tips = new List<string>();
        foreach (var value in values)
        {
            var status = value.Ambiguous ? locale.Text(UiText.UsageStripAmbiguous)
                : failed.Contains(value.Provider) ? locale.Text(UiText.UsageStripFetchFailed)
                : value.Percentage is null ? locale.Text(UiText.UsageStripWaiting)
                : value.Stale ? locale.Text(UiText.Stale) : locale.Text(UiText.UsageStripFresh);
            if (value.Percentage is { } percent)
            {
                var text = $"{value.Provider} {percent.ToString("0.#", locale.Culture)}%";
                items.Add(new(text, value.Stale || failed.Contains(value.Provider)));
                tips.Add(text + " · " + status);
            }
            else tips.Add(value.Provider + " · " + status);
            if (value.ObservedAt is { } observed)
                tips.Add(locale.Format(UiText.UsageStripLastFetch,
                    observed.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", locale.Culture) + " JST"));
        }
        if (items.Count == 0) items.Add(new(locale.Text(UiText.UsageStripWaiting), false));
        tips.Add(locale.Text(UiText.UsageStripNotes));
        return new(items, string.Join("  |  ", items.Select(item => item.Text)), string.Join("\n", tips));
    }
}
