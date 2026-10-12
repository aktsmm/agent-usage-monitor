using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
                var now = JapaneseCalendar.DayStart(DateTimeOffset.UtcNow);
                var key = new QuotaKey(AccountKey.FromIdentity(Provider.Copilot, "synthetic-ui"), "monthly demo");
                var options = new HistoryOptions(1, "00000000000000000000000000000001", "Synthetic demo folder");
                var catalog = new List<HistoryCatalogItem> { new(key, now.AddDays(-30), now) };
                var window = new HistoryWindow((request, _) =>
                {
                    if (request.Action == HistoryAction.Catalog)
                        return Task.FromResult(new HistoryResponse(true, Options: options,
                            Status: new(0, null), Catalog: catalog));
                    if (request.Action != HistoryAction.Query || request.Query is not { } query)
                        return Task.FromResult(new HistoryResponse(false, "Smoke does not mutate data"));
                    var records = Enumerable.Range(0, query.Through.DayNumber - query.From.DayNumber + 1)
                        .Select(i =>
                        {
                            var day = query.From.AddDays(i);
                            decimal? amount = i % 7 == 2 ? null : i % 7 == 3 ? 0 : 1 + i % 5;
                            var slice = new DailyHistorySlice(day, key, "synthetic-period", "Synthetic plan",
                                UsageUnit.PercentagePoints, new(WindowKind.Monthly, now.AddDays(-30), now.AddDays(1)),
                                amount, 100m / 31, amount is null ? Coverage.Unmeasured
                                    : i % 7 == 4 ? Coverage.Partial : Coverage.CompleteToObservation,
                                now.AddDays(i - 6), now.AddDays(i - 6).AddHours(8), []);
                            return new HistoryDay(day, [slice]);
                        }).ToList();
                    return Task.FromResult(new HistoryResponse(true, Days: records));
                }) { ShowActivated = false };
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
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
