using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsageTray.Core;
using AiUsageTray.Desktop;

static class HistoryBucketSmoke
{
    // Runs inside the history smoke's Application: WPF allows only one Application instance per process.
    public static async Task RunAsync()
    {
        var now = JapaneseCalendar.DayStart(DateTimeOffset.UtcNow);
        var definition = new QuotaWindow(WindowKind.Monthly, now.AddDays(-30), now.AddDays(1));
        var accountA = AccountKey.FromIdentity(Provider.Copilot, "bucket-smoke-a");
        var accountB = AccountKey.FromIdentity(Provider.Copilot, "bucket-smoke-b");
        var accountC = AccountKey.FromIdentity(Provider.Copilot, "bucket-smoke-c");
        var primary = QuotaVisibility.CopilotPrimaryBucket;
        var keys = new[]
        {
            new QuotaKey(accountA, "chat"), new QuotaKey(accountA, "completions"), new QuotaKey(accountA, primary),
            new QuotaKey(accountB, "chat"), new QuotaKey(accountB, "completions"), new QuotaKey(accountB, primary),
            // An account that reports no premium bucket must never disappear from the screen.
            new QuotaKey(accountC, "chat"), new QuotaKey(accountC, "completions")
        };
        bool showAll = false;
        var window = new HistoryWindow((request, _) =>
        {
            if (request.Action == HistoryAction.Overview)
            {
                var items = keys.Select(key => new HistoryOverviewItem(key, "p", "Plan", UsageUnit.PercentagePoints,
                    definition, 1m, 1, 0, 0)).ToList();
                return Task.FromResult(new HistoryResponse(true, Options: new HistoryOptions(1,
                    "00000000000000000000000000000001", "Synthetic"), Status: new(0, null),
                    Overview: new HistoryOverviewPage(items, null, "s")));
            }
            if (request.Action != HistoryAction.QueryPage || request.Query is not { } query)
                return Task.FromResult(new HistoryResponse(false, "Smoke does not mutate data"));
            var days = Enumerable.Range(0, query.Through.DayNumber - query.From.DayNumber + 1).Select(i =>
            {
                var day = query.From.AddDays(i);
                var slice = new DailyHistorySlice(day, query.Key, "p", "Plan", UsageUnit.PercentagePoints, definition,
                    1m, 1m, Coverage.CompleteToObservation, now.AddDays(i - 6), now.AddDays(i - 6).AddHours(8), [])
                { CumulativeCaptured = true };
                // One stray day of another unit, as the API sometimes reports a bucket as unlimited for a moment.
                return query.Key.Account == accountA && query.Key.Bucket == primary && i == 0
                    ? new HistoryDay(day, [slice, slice with { PeriodId = "sparse", Unit = UsageUnit.Credits }])
                    : new HistoryDay(day, [slice]);
            }).ToList();
            return Task.FromResult(new HistoryResponse(true, DaysPage: new HistoryDayPage(days, null, "s")));
        }, accountName: key => key.Id[..4], showAllBuckets: () => showAll) { ShowActivated = false };
        var loaded = new TaskCompletionSource();
        window.Loaded += (_, _) => loaded.TrySetResult();
        window.Show();
        try
        {
            await loaded.Task;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Descendants(window).OfType<Expander>().Single().IsExpanded = true;
            window.UpdateLayout();
            var overview = Descendants(window).OfType<ListBox>().Single(list =>
                list.Items.OfType<ListBoxItem>().Any(item => item.Tag is HistoryOverviewItem));
            var accounts = Descendants(window).OfType<ComboBox>().Single(box =>
                box.Items.OfType<ComboBoxItem>().Any(item => item.Tag is AccountKey));
            var buckets = Descendants(window).OfType<ComboBox>().Single(box =>
                box.Items.OfType<ComboBoxItem>().Any(item => item.Tag is QuotaKey));
            var range = Descendants(window).OfType<ComboBox>().Single(box =>
                box.Items.OfType<ComboBoxItem>().Any(item => item.Tag is int days && days == 90));
            QuotaKey[] Rows() => overview.Items.OfType<ListBoxItem>().Select(item => ((HistoryOverviewItem)item.Tag).Key).ToArray();
            string[] Offered() => buckets.Items.OfType<ComboBoxItem>().Select(item => ((QuotaKey)item.Tag).Bucket).ToArray();
            string? Selected() => ((buckets.SelectedItem as ComboBoxItem)?.Tag as QuotaKey)?.Bucket;
            void Switch(AccountKey account) => accounts.SelectedItem = accounts.Items.OfType<ComboBoxItem>()
                .Single(item => Equals(item.Tag, account));
            void Pick(string bucket) => buckets.SelectedItem = buckets.Items.OfType<ComboBoxItem>()
                .Single(item => ((QuotaKey)item.Tag).Bucket == bucket);

            var rows = Rows();
            if (rows.Length != 4 || rows.Count(key => key.Account == accountA) != 1 ||
                rows.Count(key => key.Account == accountB) != 1 || rows.Count(key => key.Account == accountC) != 2 ||
                rows.Where(key => key.Account != accountC).Any(key => key.Bucket != primary))
                throw new InvalidOperationException("Default history overview is not limited to the premium bucket");
            Switch(accountA);
            if (!Offered().SequenceEqual([primary]) || Selected() != primary)
                throw new InvalidOperationException("Default history bucket list is not premium only");
            var periods = Descendants(window).OfType<ComboBox>().Single(box =>
                box.Items.OfType<ComboBoxItem>().Any(item => item.Tag?.GetType().Name == "PeriodSelection"));
            var periodItems = periods.Items.OfType<ComboBoxItem>().ToArray();
            var few = new UiLocalizer().Text(UiText.PeriodFewRecords);
            if (periodItems.Length != 2 || periodItems[0].Tag!.ToString()!.Contains("sparse", StringComparison.Ordinal) ||
                periodItems[0].Content!.ToString()!.Contains(few, StringComparison.Ordinal) ||
                !periodItems[1].Content!.ToString()!.Contains(few, StringComparison.Ordinal) ||
                periods.SelectedItem != periodItems[0])
                throw new InvalidOperationException("The period with the most observed days is not first, selected and unflagged");
            if (!Descendants(window).OfType<TextBlock>().Any(text =>
                    text.Text == new UiLocalizer().Text(UiText.PeriodUnitsExplanation)))
                throw new InvalidOperationException("The pt and credit explanation is missing");
            Switch(accountB);
            if (Selected() != primary)
                throw new InvalidOperationException("Switching accounts moved the selection away from premium");
            Switch(accountC);
            if (!Offered().OrderBy(name => name).SequenceEqual(["chat", "completions"]) || Selected() is null)
                throw new InvalidOperationException("An account without a premium bucket is not shown");
            Switch(accountA);
            if (Selected() != primary)
                throw new InvalidOperationException("Returning to a premium account did not select premium");

            showAll = true;
            range.SelectedIndex = range.SelectedIndex == 0 ? 1 : 0;
            if (Rows().Length != keys.Length)
                throw new InvalidOperationException("Showing all Copilot buckets did not restore chat and completions");
            Switch(accountA);
            if (Offered().Length != 3)
                throw new InvalidOperationException("All buckets are not offered when requested");
            Pick(primary);
            Switch(accountB);
            if (Selected() != primary)
                throw new InvalidOperationException("Account switch with all buckets did not keep premium");
            Pick("completions");
            Switch(accountA);
            if (Selected() != "completions")
                throw new InvalidOperationException("Account switch did not keep the chosen bucket by name");
            Switch(accountC);
            Pick("chat");
            Switch(accountB);
            if (Selected() != "chat")
                throw new InvalidOperationException("Account switch did not keep chat when chosen");

            // Turning the setting off while the window is open applies when the window is activated again.
            showAll = false;
            typeof(Window).GetMethod("OnActivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [EventArgs.Empty]);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            rows = Rows();
            if (rows.Length != 4 || rows.Where(key => key.Account != accountC).Any(key => key.Bucket != primary))
                throw new InvalidOperationException("Activating the window did not apply the changed Copilot bucket setting");
            if (((accounts.SelectedItem as ComboBoxItem)?.Tag as AccountKey) != accountB || Selected() != primary)
                throw new InvalidOperationException("Hiding the selected bucket moved to another account instead of its premium bucket");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
