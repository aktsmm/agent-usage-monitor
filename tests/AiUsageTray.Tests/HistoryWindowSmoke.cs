using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AiUsageTray.Core;
using AiUsageTray.Desktop;

static class HistoryWindowSmoke
{
    public static Task RunAsync(string? outputFolder)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var (name, color) in new[]
                {
                    ("BackgroundBrush", "#292929"), ("SoftBrush", "#2E2E2E"),
                    ("TextBrush", "#DEDEDE"), ("MutedBrush", "#B0B0B0"),
                    ("BorderBrush", "#474747"), ("AccentBrush", "#FD8EA1"), ("WarningBrush", "#FBBF24")
                })
                    app.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                var overviewWindow = new MainWindow(null!);
                var labelMethod = typeof(MainWindow).GetMethod("Label",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Overview label factory is missing");
                var amountLabel = (TextBlock)labelMethod.Invoke(overviewWindow,
                    ["0.0000000000000000000000000001 credits", 21d, true])!;
                amountLabel.Measure(new Size(180, double.PositiveInfinity));
                if (amountLabel.TextWrapping != TextWrapping.Wrap || amountLabel.DesiredSize.Height < 40)
                    throw new InvalidOperationException("Full-precision overview amount does not wrap");
                var overviewError = (TextBlock)overviewWindow.FindName("Error");
                overviewError.Text = new UiLocalizer(DisplayLanguage.English).Text(UiText.HistoryIntervalsOverlap);
                overviewError.Measure(new Size(180, double.PositiveInfinity));
                if (overviewError.TextWrapping != TextWrapping.Wrap || overviewError.DesiredSize.Height < 30)
                    throw new InvalidOperationException("Overview history error does not wrap");
                var now = JapaneseCalendar.DayStart(DateTimeOffset.UtcNow);
                var key = new QuotaKey(AccountKey.FromIdentity(Provider.Copilot, "synthetic-ui"), "monthly demo");
                var otherKey = new QuotaKey(AccountKey.FromIdentity(Provider.Codex, "synthetic-ui-other"), "weekly demo");
                var missingKey = new QuotaKey(AccountKey.FromIdentity(Provider.Claude, "synthetic-ui-missing"), "missing demo");
                var tinyKey = new QuotaKey(AccountKey.FromIdentity(Provider.Claude, "synthetic-ui-tiny"), "tiny demo");
                var windowDefinition = new QuotaWindow(WindowKind.Monthly, now.AddDays(-30), now.AddDays(1));
                HistoryQuery? lastQuery = null;
                bool failOverview = false, failQuery = false, mismatchQueryPage = false, singleProvider = false;
                bool paginateOverview = false, mismatchOverviewPage = false, skipOverviewItem = false, emptyOverviewPage = false;
                bool skipQueryItem = false, blankOverviewSnapshot = false, blankQuerySnapshot = false;
                bool oversizedOverview = false, oversizedSecondPage = false, failCatalog = false;
                TaskCompletionSource? detailGate = null;
                async Task<HistoryResponse> WaitForDetailAsync(Task gate, HistoryResponse response)
                {
                    await gate;
                    return response;
                }
                int queryPages = 0, overviewPages = 0;
                var options = new HistoryOptions(1, "00000000000000000000000000000001", "Synthetic demo folder");
                var window = new HistoryWindow((request, _) =>
                {
                    if (request.Action == HistoryAction.Overview)
                    {
                        overviewPages++;
                        if (oversizedOverview) return Task.FromResult(new HistoryResponse(false, "ipc_message_too_large"));
                        if (oversizedSecondPage && request.Offset > 0)
                            return Task.FromResult(new HistoryResponse(false, "ipc_message_too_large"));
                        if (failOverview) return Task.FromResult(new HistoryResponse(false, "history_storage_unavailable"));
                        var count = request.Through!.Value.DayNumber - request.From!.Value.DayNumber + 1;
                        var values = Enumerable.Range(0, count).Where(i => i % 7 != 2).ToArray();
                        var first = values.Where(i => i % 2 == 0).ToArray();
                        var second = values.Where(i => i % 2 != 0).ToArray();
                        decimal Amount(int i) => i % 7 == 3 ? 0 : 1 + i % 5;
                        var items = new List<HistoryOverviewItem>
                        {
                            new(key, "synthetic-period", "Synthetic plan", UsageUnit.PercentagePoints,
                                windowDefinition, first.Sum(Amount), first.Length, 1, 1),
                             new(key, "synthetic-second", "Synthetic plan", UsageUnit.PercentagePoints,
                                windowDefinition, second.Sum(Amount) + (count > 6 ? 0.25m : 0),
                                second.Length + (count > 6 ? 1 : 0), 1, 1),
                             new(otherKey, "synthetic-period", "Synthetic plan", UsageUnit.PercentagePoints,
                                windowDefinition, 0, values.Length, 1, 1),
                             new(missingKey, null, null, UsageUnit.Unknown, null, null, 0, 0, 0),
                             new(tinyKey, "synthetic-period", "Synthetic plan", UsageUnit.Credits,
                                windowDefinition, values.Count(i => i % 7 != 3) * 0.000000001m, values.Length, 1, 1)
                        };
                        if (singleProvider) items.RemoveAll(item => item.Key.Account.Provider != Provider.Copilot);
                        if (emptyOverviewPage && request.Offset == 0)
                            return Task.FromResult(new HistoryResponse(true, Overview: new HistoryOverviewPage([], 1, "synthetic")));
                        if (blankOverviewSnapshot)
                            return Task.FromResult(new HistoryResponse(true, Overview: new HistoryOverviewPage(items, null, "")));
                        if (paginateOverview || mismatchOverviewPage || skipOverviewItem || oversizedSecondPage)
                        {
                            var firstPage = request.Offset == 0;
                            return Task.FromResult(new HistoryResponse(true, Overview: new HistoryOverviewPage(
                                firstPage ? items.Take(2).ToList() : items.Skip(request.Offset).ToList(),
                                firstPage ? skipOverviewItem ? 3 : 2 : null,
                                mismatchOverviewPage && !firstPage ? "different" : "synthetic")));
                        }
                        return Task.FromResult(new HistoryResponse(true, Options: options,
                            Status: new(0, null), Overview: new HistoryOverviewPage(items, null, "synthetic")));
                    }
                    if (request.Action == HistoryAction.Catalog)
                    {
                        if (failCatalog) return Task.FromResult(new HistoryResponse(false, "history_storage_unavailable"));
                        return Task.FromResult(new HistoryResponse(true, Catalog:
                        [
                            new(key, now, now), new(otherKey, now, now),
                            new(missingKey, now, now), new(tinyKey, now, now)
                        ]));
                    }
                    if (request.Action != HistoryAction.QueryPage || request.Query is not { } query)
                        return Task.FromResult(new HistoryResponse(false, "Smoke does not mutate data"));
                    if (failQuery) return Task.FromResult(new HistoryResponse(false, "history_storage_unavailable"));
                    lastQuery = query;
                    queryPages++;
                    var records = Enumerable.Range(0, query.Through.DayNumber - query.From.DayNumber + 1)
                        .Select(i =>
                        {
                            var day = query.From.AddDays(i);
                            if (query.Key == missingKey) return new HistoryDay(day, []);
                            decimal? amount = i % 7 == 2 ? null :
                                query.Key == otherKey || i % 7 == 3 ? 0 : query.Key == tinyKey ? 0.000000001m : 1 + i % 5;
                            var periodId = query.Key == key && i % 2 != 0 ? "synthetic-second" : "synthetic-period";
                            var slice = new DailyHistorySlice(day, query.Key, periodId, "Synthetic plan",
                                query.Key == tinyKey ? UsageUnit.Credits : UsageUnit.PercentagePoints, windowDefinition,
                                amount, query.Key == tinyKey ? 0.00001m : 100m / 31, amount is null ? Coverage.Unmeasured
                                    : i % 7 == 4 ? Coverage.Partial : Coverage.CompleteToObservation,
                                now.AddDays(i - 6), now.AddDays(i - 6).AddHours(8), []);
                            return query.Key == key && i == 6
                                ? new HistoryDay(day, [slice, slice with { PeriodId = "synthetic-second", Observed = 0.25m }])
                                : new HistoryDay(day, [slice]);
                        }).ToList();
                    var fragments = records.SelectMany(day => day.Slices.Count == 0 ? [day] :
                        day.Slices.Select(slice => new HistoryDay(day.Date, [slice]))).ToList();
                    var end = Math.Min(request.Offset + 7, fragments.Count);
                    var detailResponse = new HistoryResponse(true, DaysPage: new HistoryDayPage(
                        fragments.Skip(request.Offset).Take(end - request.Offset).ToList(),
                        end < fragments.Count ? end + (skipQueryItem ? 1 : 0) : null,
                        blankQuerySnapshot ? "" : mismatchQueryPage && request.Offset > 0 ? "different" : "synthetic"));
                    return detailGate is { } gate && request.Offset == 0
                        ? WaitForDetailAsync(gate.Task, detailResponse) : Task.FromResult(detailResponse);
                }) { ShowActivated = false };
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        var tabs = Descendants(window).OfType<TabControl>().Single();
                        tabs.SelectedIndex = 1;
                        window.UpdateLayout();
                        window.SetStorageStatus(new HistoryStorageStatus(1, "history_consumption_intervals_overlap"));
                        foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
                        {
                            window.SetLanguage(new UiLocalizer(language));
                            var expected = language == DisplayLanguage.Japanese
                                ? "観測区間が重複" : "observation intervals overlap";
                            if (!Descendants(window).OfType<TextBlock>().Any(text =>
                                text.Text.Contains(expected, StringComparison.Ordinal) &&
                                text.Text.Contains("history_consumption_intervals_overlap", StringComparison.Ordinal)))
                                throw new InvalidOperationException("Localized pending overlap status missing");
                        }
                        window.SetStorageStatus(new HistoryStorageStatus(0, null));
                        window.SetLanguage(new UiLocalizer(DisplayLanguage.English));
                        tabs.SelectedIndex = 0;
                        window.UpdateLayout();
                        var overview = Descendants(window).OfType<ListBox>().Single(list =>
                            list.Items.OfType<ListBoxItem>().Any(item => item.Tag is HistoryOverviewItem));
                        var initialGraph = Descendants(window).OfType<Canvas>().Single();
                        int graphRevealRequests = 0;
                        RequestBringIntoViewEventHandler revealHandler = (_, e) =>
                        {
                            if (ReferenceEquals(e.TargetObject, initialGraph))
                            {
                                graphRevealRequests++;
                                e.Handled = true;
                            }
                        };
                        window.AddHandler(FrameworkElement.RequestBringIntoViewEvent, revealHandler, true);
                        var other = overview.Items.OfType<ListBoxItem>()
                            .Single(item => (item.Tag as HistoryOverviewItem)?.Key == otherKey);
                        var inactiveFocus = Keyboard.FocusedElement;
                        var startedInactive = !window.IsActive;
                        detailGate = new();
                        other.IsSelected = true;
                        if (!window.IsBusy) throw new InvalidOperationException("Delayed detail did not remain busy");
                        detailGate.SetResult();
                        detailGate = null;
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (window.IsBusy) throw new InvalidOperationException("Delayed detail did not complete");
                        if (graphRevealRequests != 0)
                            throw new InvalidOperationException("Loading a selection unexpectedly scrolled to the graph");
                        if (startedInactive && !window.IsActive && Keyboard.FocusedElement != inactiveFocus)
                            throw new InvalidOperationException("Background history loading changed keyboard focus");
                        if (lastQuery?.Key != otherKey)
                            throw new InvalidOperationException("Overview did not open the selected account's graph");
                        if (queryPages < 2)
                            throw new InvalidOperationException("Paged history detail was not loaded");
                        var showGraph = Descendants(window).OfType<Button>().Single(button =>
                            button.Content?.ToString() == new UiLocalizer(DisplayLanguage.English).Text(UiText.HistoryShowGraph) ||
                            button.Content?.ToString() == new UiLocalizer(DisplayLanguage.Japanese).Text(UiText.HistoryShowGraph));
                        if (!showGraph.IsEnabled) throw new InvalidOperationException("Graph navigation is disabled after loading");
                        showGraph.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (graphRevealRequests != 1)
                            throw new InvalidOperationException("Graph navigation did not reveal the graph");
                        window.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, revealHandler);
                        if (!Descendants(window).OfType<TextBlock>().Any(t =>
                            t.Text == new UiLocalizer(DisplayLanguage.English).Text(UiText.HistoryGraphZero) ||
                            t.Text == new UiLocalizer(DisplayLanguage.Japanese).Text(UiText.HistoryGraphZero)))
                            throw new InvalidOperationException("Zero graph explanation missing");
                        overview.Items.OfType<ListBoxItem>()
                            .Single(item => (item.Tag as HistoryOverviewItem)?.Key == missingKey).IsSelected = true;
                        if (lastQuery?.Key != missingKey || !Descendants(window).OfType<TextBlock>().Any(t =>
                            t.Text == new UiLocalizer(DisplayLanguage.English).Text(UiText.HistoryGraphNoData) ||
                            t.Text == new UiLocalizer(DisplayLanguage.Japanese).Text(UiText.HistoryGraphNoData)))
                            throw new InvalidOperationException("Unmeasured graph explanation missing");
                        var tiny = overview.Items.OfType<ListBoxItem>()
                            .Single(item => (item.Tag as HistoryOverviewItem)?.Key == tinyKey);
                        if (!tiny.Content.ToString()!.Contains("0.000", StringComparison.Ordinal))
                            throw new InvalidOperationException("Small positive amount was rounded to zero");
                        tiny.IsSelected = true;
                        var smallValueGraph = Descendants(window).OfType<Canvas>().Single();
                        foreach (var language in new[] { DisplayLanguage.English, DisplayLanguage.Japanese })
                        {
                            window.SetLanguage(new UiLocalizer(language));
                            if (showGraph.Content?.ToString() != new UiLocalizer(language).Text(UiText.HistoryShowGraph))
                                throw new InvalidOperationException("Graph navigation was not localized");
                            var tooltip = smallValueGraph.Children.OfType<Button>()
                                .First().ToolTip!.ToString()!;
                            if (!tooltip.Contains("0.000000001", StringComparison.Ordinal) ||
                                !tooltip.Contains("0.00001", StringComparison.Ordinal) ||
                                !tooltip.Contains("0.01%", StringComparison.Ordinal))
                                throw new InvalidOperationException($"Small positive detail values were rounded to zero: {tooltip}");
                            var bars = smallValueGraph.Children.OfType<Button>().ToArray();
                            if (!bars[2].ToolTip!.ToString()!.Contains("—%", StringComparison.Ordinal) ||
                                !bars[3].ToolTip!.ToString()!.Contains("0%", StringComparison.Ordinal))
                                throw new InvalidOperationException("Missing and zero guidance usage were not preserved");
                            foreach (var bar in new[] { bars[0], bars[2], bars[3] })
                            {
                                var barPeer = UIElementAutomationPeer.CreatePeerForElement(bar);
                                if (barPeer is null || barPeer.GetName() != bar.ToolTip!.ToString() ||
                                    barPeer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider)
                                    throw new InvalidOperationException("History details are not accessible to automation");
                            }
                            window.UpdateLayout();
                            DependencyObject? hit = smallValueGraph.InputHitTest(
                                new Point(Canvas.GetLeft(bars[3]) + bars[3].ActualWidth / 2, 18)) as DependencyObject;
                            while (hit is not null && hit != bars[3]) hit = VisualTreeHelper.GetParent(hit);
                            if (hit != bars[3] || bars[3].Height <= (double)bars[3].Tag)
                                throw new InvalidOperationException("Zero bar does not have a full-height clickable target");
                            if (bars[3].Template.FindName("Bar", bars[3]) is not Border drawn ||
                                drawn.Height != (double)bars[3].Tag ||
                                smallValueGraph.Children.OfType<System.Windows.Shapes.Line>().Any(line => line.IsHitTestVisible))
                                throw new InvalidOperationException("History bar rendering or guidance hit-testing changed");
                        }
                        overview.Items.OfType<ListBoxItem>()
                            .Single(item => item.Tag is HistoryOverviewItem itemData &&
                                itemData.Key == key && itemData.PeriodId == "synthetic-second").IsSelected = true;
                        if (lastQuery?.Key != key)
                            throw new InvalidOperationException("Overview did not return to the original graph");
                        var graph = Descendants(window).OfType<Canvas>().Single();
                        var splitDay = graph.Children.OfType<Button>().ElementAt(6);
                        if (!splitDay.ToolTip!.ToString()!.Contains("0.25", StringComparison.Ordinal))
                            throw new InvalidOperationException("Split-day detail was not merged across pages");
                        var peer = UIElementAutomationPeer.CreatePeerForElement(splitDay);
                        if (peer?.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invocation ||
                            peer.GetName() != splitDay.ToolTip!.ToString() || !splitDay.Focusable || !splitDay.IsTabStop)
                            throw new InvalidOperationException("History bar lacks an actionable automation peer");
                        invocation.Invoke();
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (splitDay.BorderThickness.Left != 3 || !Descendants(window).OfType<TextBlock>()
                                .Any(text => text.Text == splitDay.ToolTip!.ToString()))
                            throw new InvalidOperationException("Automation invocation did not select the history day");
                        var period = Descendants(window).OfType<ComboBox>().Single(box =>
                            box.Items.OfType<ComboBoxItem>().Count(item =>
                                item.Content?.ToString()?.Contains("Synthetic plan", StringComparison.Ordinal) == true) == 2);
                        period.SelectedIndex = 0;
                        if ((overview.SelectedItem as ListBoxItem)?.Tag is not HistoryOverviewItem selection ||
                            selection.Key != key || selection.PeriodId == "synthetic-second")
                            throw new InvalidOperationException("Period change did not synchronize the overview");
                        foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
                        foreach (bool light in new[] { false, true })
                        {
                            foreach (var (name, day, night) in new[]
                            {
                                ("BackgroundBrush", "#FFFFFF", "#292929"), ("SoftBrush", "#F5F5F5", "#2E2E2E"),
                                ("TextBrush", "#242424", "#DEDEDE"), ("MutedBrush", "#5C5C5C", "#B0B0B0"),
                                ("BorderBrush", "#DEDEDE", "#474747"), ("AccentBrush", "#B11F4B", "#FD8EA1"),
                                ("WarningBrush", "#B45309", "#FBBF24")
                            })
                                app.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? day : night));
                            var locale = new UiLocalizer(language);
                            window.SetLanguage(locale);
                            window.UpdateLayout();
                            var canvases = Descendants(window).OfType<Canvas>().ToArray();
                            if (canvases.Length != 1 || canvases[0].Children.Count < 15)
                                throw new InvalidOperationException("History graph did not render");
                            var lists = Descendants(window).OfType<ListBox>().ToArray();
                            if (!lists.Any(list => list.Items.OfType<ListBoxItem>().Any(item =>
                                item.Tag is HistoryOverviewItem && item.IsSelected)))
                                throw new InvalidOperationException("History overview selection missing");
                            var buttons = Descendants(window).OfType<Button>().Select(b => b.Content?.ToString()).ToArray();
                            if (!buttons.Contains(locale.Text(UiText.ShowHistoryFile)) ||
                                !buttons.Contains(locale.Text(UiText.OpenHistoryFolder)))
                                throw new InvalidOperationException("Localized history file navigation missing");
                            if (outputFolder is not null)
                            {
                                Directory.CreateDirectory(outputFolder);
                                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
                                    96, 96, PixelFormats.Pbgra32);
                                bitmap.Render(window);
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                using var stream = File.Create(Path.Combine(outputFolder,
                                    $"history-window-{locale.LanguageTag}-{(light ? "light" : "dark")}-synthetic.png"));
                                encoder.Save(stream);
                            }
                        }
                        var range = Descendants(window).OfType<ComboBox>().Single(box =>
                            box.Items.OfType<ComboBoxItem>().Any(item => item.Tag is int days && days == 90));
                        range.SelectedIndex = 0;
                        if (lastQuery is null || lastQuery.Key != key ||
                            lastQuery.Through.DayNumber - lastQuery.From.DayNumber + 1 != 7)
                            throw new InvalidOperationException("Seven-day range did not preserve the selection");
                        range.SelectedIndex = 2;
                        if (lastQuery is null || lastQuery.Through.DayNumber - lastQuery.From.DayNumber + 1 != 90)
                            throw new InvalidOperationException("Ninety-day range was not queried");
                        singleProvider = true;
                        range.SelectedIndex = 0;
                        var provider = Descendants(window).OfType<ComboBox>().Single(box =>
                            box.Items.OfType<Provider>().Contains(Provider.Copilot));
                        var filters = Descendants(window).OfType<ComboBox>().Where(box => box != range).ToArray();
                        if (filters.Length != 4)
                            throw new InvalidOperationException("History filter controls changed unexpectedly");
                        provider.SelectedItem = Provider.Codex;
                        if (overview.SelectedItem is not null)
                            throw new InvalidOperationException("An empty provider left a stale overview selection");
                        var pagesBeforeReturn = queryPages;
                        overview.Items.OfType<ListBoxItem>().First().IsSelected = true;
                        if (lastQuery?.Key != key || queryPages == pagesBeforeReturn)
                            throw new InvalidOperationException("The selected graph could not be reopened after an empty provider");
                        singleProvider = false;
                        range.SelectedIndex = 2;
                        bool HasError(string code) => Descendants(window).OfType<TextBlock>()
                            .Any(text => text.Text.StartsWith(
                                new UiLocalizer(DisplayLanguage.English).Error(code), StringComparison.Ordinal));
                        oversizedOverview = true;
                        range.SelectedIndex = 0;
                        if (overview.Items.Count != 0 || filters.Any(filter => !filter.IsEnabled) ||
                            !HasError("ipc_message_too_large"))
                            throw new InvalidOperationException("Oversized overview did not expose the fresh catalog and error");
                        provider.SelectedItem = Provider.Codex;
                        if (lastQuery?.Key != otherKey || graph.Children.Count == 0 || !HasError("ipc_message_too_large"))
                            throw new InvalidOperationException("Oversized overview blocked an unrelated account");
                        foreach (var language in new[] { DisplayLanguage.Japanese, DisplayLanguage.English })
                        {
                            var localizer = new UiLocalizer(language);
                            window.SetLanguage(localizer);
                            if (!Descendants(window).OfType<TextBlock>().Any(text =>
                                text.Text.Contains(localizer.Text(UiText.HistoryOverviewFallback), StringComparison.Ordinal)))
                                throw new InvalidOperationException("Localized overview recovery instruction missing");
                        }
                        oversizedOverview = false;
                        range.SelectedIndex = 2;
                        if ((overview.SelectedItem as ListBoxItem)?.Tag is not HistoryOverviewItem recovered ||
                            recovered.Key != otherKey || lastQuery?.Key != otherKey)
                            throw new InvalidOperationException("Overview reload did not preserve the fallback account");
                        overview.Items.OfType<ListBoxItem>().First(item => (item.Tag as HistoryOverviewItem)?.Key == key)
                            .IsSelected = true;
                        oversizedSecondPage = true;
                        range.SelectedIndex = 1;
                        if (overview.Items.Count != 0 || filters.Any(filter => !filter.IsEnabled) ||
                            !HasError("ipc_message_too_large"))
                            throw new InvalidOperationException("Oversized continuation left partial overview rows");
                        oversizedSecondPage = false;
                        range.SelectedIndex = 2;
                        oversizedOverview = true;
                        failCatalog = true;
                        range.SelectedIndex = 1;
                        if (filters.Any(filter => filter.IsEnabled) || graph.Children.Count != 0 ||
                            !HasError("history_storage_unavailable"))
                            throw new InvalidOperationException("Failed catalog fallback left stale filters or graph");
                        oversizedOverview = false;
                        failCatalog = false;
                        range.SelectedIndex = 2;
                        paginateOverview = true;
                        var overviewPagesBeforeValid = overviewPages;
                        range.SelectedIndex = 0;
                        if (overviewPages != overviewPagesBeforeValid + 2 || overview.Items.Count != 5 || graph.Children.Count == 0)
                            throw new InvalidOperationException("Valid overview pages were not combined");
                        paginateOverview = false;
                        range.SelectedIndex = 1;
                        mismatchOverviewPage = true;
                        range.SelectedIndex = 0;
                        if (overview.Items.Count != 0 || graph.Children.Count != 0 || !HasError("history_overview_changed"))
                            throw new InvalidOperationException("Changed overview snapshot was not rejected");
                        mismatchOverviewPage = false;
                        range.SelectedIndex = 1;
                        skipOverviewItem = true;
                        range.SelectedIndex = 2;
                        if (overview.Items.Count != 0 || graph.Children.Count != 0 || !HasError("history_response_missing"))
                            throw new InvalidOperationException("Skipping an overview item was not rejected");
                        skipOverviewItem = false;
                        range.SelectedIndex = 1;
                        emptyOverviewPage = true;
                        var overviewPagesBeforeEmpty = overviewPages;
                        range.SelectedIndex = 2;
                        if (overviewPages != overviewPagesBeforeEmpty + 1 || overview.Items.Count != 0 ||
                            graph.Children.Count != 0 || !HasError("history_response_missing"))
                            throw new InvalidOperationException("Empty overview continuation was not rejected immediately");
                        emptyOverviewPage = false;
                        range.SelectedIndex = 1;
                        blankOverviewSnapshot = true;
                        range.SelectedIndex = 2;
                        if (overview.Items.Count != 0 || graph.Children.Count != 0 || !HasError("history_response_missing"))
                            throw new InvalidOperationException("Missing overview snapshot was not rejected");
                        blankOverviewSnapshot = false;
                        range.SelectedIndex = 1;
                        mismatchQueryPage = true;
                        range.SelectedIndex = 0;
                        if (graph.Children.Count != 0 || !HasError("history_overview_changed"))
                            throw new InvalidOperationException("Changed detail snapshot left a stale graph");
                        mismatchQueryPage = false;
                        range.SelectedIndex = 1;
                        skipQueryItem = true;
                        range.SelectedIndex = 0;
                        if (graph.Children.Count != 0 || !HasError("history_response_missing"))
                            throw new InvalidOperationException("Skipping a detail fragment was not rejected");
                        skipQueryItem = false;
                        range.SelectedIndex = 1;
                        blankQuerySnapshot = true;
                        range.SelectedIndex = 0;
                        if (graph.Children.Count != 0 || !HasError("history_response_missing"))
                            throw new InvalidOperationException("Missing detail snapshot was not rejected");
                        blankQuerySnapshot = false;
                        failOverview = true;
                        range.SelectedIndex = 1;
                        if (overview.Items.Count != 0 || Descendants(window).OfType<Canvas>().Single().Children.Count != 0 ||
                            Descendants(window).OfType<TextBlock>().Any(t =>
                                t.Text == new UiLocalizer(DisplayLanguage.English).Text(UiText.HistoryLoading)))
                            throw new InvalidOperationException("Overview failure left stale or loading content");
                        if (showGraph.IsEnabled) throw new InvalidOperationException("Graph navigation remained enabled after overview failure");
                        if (filters.Any(filter => filter.IsEnabled) || lastQuery is null)
                            throw new InvalidOperationException("Overview failure left stale filters enabled");
                        failOverview = false;
                        failQuery = true;
                        range.SelectedIndex = 0;
                        if (filters.Any(filter => !filter.IsEnabled))
                            throw new InvalidOperationException("Successful overview reload did not restore filters");
                        if (Descendants(window).OfType<Canvas>().Single().Children.Count != 0 ||
                            Descendants(window).OfType<TextBlock>().Any(t =>
                                t.Text == new UiLocalizer(DisplayLanguage.English).Text(UiText.HistoryLoading)))
                            throw new InvalidOperationException("Query failure left stale or loading content");
                        if (showGraph.IsEnabled) throw new InvalidOperationException("Graph navigation remained enabled after query failure");
                        failQuery = false;
                        Descendants(window).OfType<TextBox>().Single().Text = "9999-12";
                        var showMonth = Descendants(window).OfType<Button>().Single(button =>
                            button.Content?.ToString() == new UiLocalizer(DisplayLanguage.English).Text(UiText.ShowMonth));
                        showMonth.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (lastQuery is null || lastQuery.From != new DateOnly(9999, 12, 1) ||
                            lastQuery.Through != DateOnly.MaxValue)
                            throw new InvalidOperationException("Last representable month was not queried");
                        completion.SetResult();
                    }
                    catch (Exception ex) { completion.SetException(ex); }
                    finally { window.Close(); app.Shutdown(); }
                });
                app.Run(window);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
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
