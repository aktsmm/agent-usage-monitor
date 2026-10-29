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
        => CreateByAccount(settings, new UiLocalizer(settings.Language), verified, now, maximumAge,
            failedProviders?.ToHashSet() ?? []);

    // Accounts of one service are listed side by side and never merged; the alias is shown only when a service has several.
    private static UsageStripPresentation CreateByAccount(DesktopSettings settings, UiLocalizer locale,
        IEnumerable<QuotaSnapshot> verified, DateTimeOffset now, TimeSpan maximumAge, HashSet<Provider> failed)
    {
        var values = UsageStrip.ValuesByAccount(settings.Accounts(), verified, now, maximumAge);
        var items = new List<UsageStripItem>();
        var tips = new List<string>();
        foreach (var value in values)
        {
            var label = values.Count(v => v.Provider == value.Provider) > 1 ? $"{value.Provider}·{value.Name}" : value.Provider.ToString();
            var status = value.Ambiguous ? locale.Text(UiText.UsageStripAmbiguous)
                : failed.Contains(value.Provider) ? locale.Text(UiText.UsageStripFetchFailed)
                : value.Percentage is null ? locale.Text(UiText.UsageStripWaiting)
                : value.Stale ? locale.Text(UiText.Stale) : locale.Text(UiText.UsageStripFresh);
            if (value.Percentage is { } percent)
            {
                var text = $"{label} {percent.ToString("0.#", locale.Culture)}%";
                items.Add(new(text, value.Stale || failed.Contains(value.Provider)));
                tips.Add(text + " · " + status);
            }
            else tips.Add(label + " · " + status);
            if (value.ObservedAt is { } observed)
                tips.Add(locale.Format(UiText.UsageStripLastFetch,
                    observed.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", locale.Culture) + " JST"));
        }
        if (items.Count == 0) items.Add(new(locale.Text(UiText.UsageStripWaiting), false));
        tips.Add(locale.Text(UiText.UsageStripNotes));
        return new(items, string.Join("  |  ", items.Select(item => item.Text)), string.Join("\n", tips));
    }
}
