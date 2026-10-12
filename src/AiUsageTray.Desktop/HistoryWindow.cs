using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AiUsageTray.Core;
using Microsoft.Win32;
using JapaneseCalendar = AiUsageTray.Core.JapaneseCalendar;

namespace AiUsageTray.Desktop;

public sealed class HistoryWindow : Window
{
    private readonly Func<HistoryRequest, CancellationToken, Task<HistoryResponse>> send;
    private UiLocalizer localizer;
    private readonly List<Action> translations = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly ComboBox provider = new(), account = new(), bucket = new(), range = new(), period = new();
    private readonly TextBox month = new();
    private readonly Canvas chart = new() { Height = 252, MinWidth = 680 };
    private readonly TextBlock detail = new(), error = new(), status = new(), folder = new(), archiveStatus = new();
    private readonly StackPanel history = new(), storage = new();
    private readonly Button choose = new();
    private List<HistoryCatalogItem> catalog = [];
    private List<HistoryDay> days = [];
    private bool loading;
    private bool monthRange;
    private HistoryStorageStatus? storageStatus;
    public bool IsBusy { get; private set; }
    public bool IsLocationEditing { get; private set; }

    public HistoryWindow(Func<HistoryRequest, CancellationToken, Task<HistoryResponse>> send,
        UiLocalizer? localizer = null)
    {
        this.send = send;
        this.localizer = localizer ?? new();
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AiUsageTray.Desktop;component/HistoryControls.xaml", UriKind.Relative)
        });
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        foreach (var text in new[] { detail, status, folder, archiveStatus })
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        foreach (var control in new Control[] { provider, account, bucket, range, period, month, choose })
        {
            control.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            control.SetResourceReference(Control.BackgroundProperty, "SoftBrush");
            control.Margin = new Thickness(0, 4, 4, 8);
            control.Padding = new Thickness(6);
        }
        translations.Add(() => Title = this.localizer.Text(UiText.HistoryTitle));
        Width = 960; Height = 800; MinWidth = 780; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel();
        header.Children.Add(Label(UiText.AgentUsage, 24, true));
        header.Children.Add(Label(UiText.HistorySubtitle));
        header.Children.Add(Label(UiText.CalendarExplanation, 11));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        error.Foreground = Brush("WarningBrush");
        error.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(error, Dock.Bottom); root.Children.Add(error);
        var tabs = new TabControl { Margin = new Thickness(0, 16, 0, 0) };
        tabs.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        tabs.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        tabs.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        foreach (var (key, panel) in new[] { (UiText.History, history), (UiText.HistoryStorage, storage) })
        {
            var tab = new TabItem { Content = Scroll(panel) };
            translations.Add(() => tab.Header = this.localizer.Text(key));
            tabs.Items.Add(tab);
        }
        root.Children.Add(tabs);
        Content = root;
        BuildHistory();
        BuildStorage();
        SetLanguage(this.localizer);
        Loaded += async (_, _) => await RunAsync(LoadAsync);
        Closed += (_, _) => stopping.Cancel();
        chart.SizeChanged += (_, _) => Draw();
        Activated += (_, _) => Draw();
    }

    public void SetLanguage(UiLocalizer value)
    {
        localizer = value;
        foreach (var apply in translations) apply();
        foreach (ComboBoxItem item in range.Items)
            item.Content = localizer.Format(UiText.RangeDays, item.Tag);
        foreach (ComboBoxItem item in period.Items)
            if (days.SelectMany(d => d.Slices).FirstOrDefault(s => s.PeriodId == (string)item.Tag) is { } slice)
                item.Content = PeriodTitle(slice);
        if (storageStatus is { } status) SetStorageStatus(status);
        Draw();
    }

    public void SetStorageStatus(HistoryStorageStatus value)
    {
        storageStatus = value;
        status.Text = value.Saved ? localizer.Text(UiText.Saved)
            : localizer.Format(UiText.PendingRecords, value.PendingRecords,
                value.Error is null ? "" : localizer.Error(value.Error));
    }

    private ScrollViewer Scroll(StackPanel panel)
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel, Padding = new Thickness(12)
        };
        scroll.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        scroll.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        return scroll;
    }
    private Brush Brush(string resource) => (Brush)FindResource(resource);
    private TextBlock Label(string text, double size = 13, bool bold = false)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, 6, 0, 6)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, bold ? "TextBrush" : "MutedBrush");
        return label;
    }
    private TextBlock Label(UiText key, double size = 13, bool bold = false)
    {
        var label = Label("", size, bold);
        translations.Add(() => label.Text = localizer.Text(key));
        return label;
    }
    private void Field(Panel panel, UiText key, Control control)
    {
        var column = new StackPanel { Margin = new Thickness(0, 0, 16, 0), MinWidth = 180 };
        column.Children.Add(Label(key));
        translations.Add(() => AutomationProperties.SetName(control, localizer.Text(key)));
        column.Children.Add(control);
        panel.Children.Add(column);
    }
    private Button ActionButton(UiText key, Func<Task> action)
    {
        var button = new Button { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4) };
        translations.Add(() =>
        {
            button.Content = localizer.Text(key);
            AutomationProperties.SetName(button, localizer.Text(key));
        });
        button.SetResourceReference(Control.BackgroundProperty, "SoftBrush");
        button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        button.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private void BuildHistory()
    {
        var filters = new WrapPanel();
        foreach (var value in Enum.GetValues<Provider>()) provider.Items.Add(value);
        Field(filters, UiText.Service, provider);
        Field(filters, UiText.AccountHash, account);
        Field(filters, UiText.Quota, bucket);
        history.Children.Add(filters);
        provider.SelectionChanged += (_, _) => { if (!loading) PopulateAccounts(); };
        account.SelectionChanged += (_, _) => { if (!loading) PopulateBuckets(); };
        bucket.SelectionChanged += async (_, _) => { if (!loading) await RunAsync(QueryAsync); };
        var controls = new WrapPanel();
        foreach (int value in new[] { 7, 30, 90 }) range.Items.Add(new ComboBoxItem { Tag = value });
        range.SelectedIndex = 0;
        Field(controls, UiText.Range, range);
        month.Text = DailyHistory.Month(JapaneseCalendar.Date(DateTimeOffset.UtcNow));
        Field(controls, UiText.MonthInput, month);
        controls.Children.Add(ActionButton(UiText.ShowMonth, async () => { monthRange = true; await QueryAsync(); }));
        controls.Children.Add(ActionButton(UiText.Reload, LoadAsync));
        history.Children.Add(controls);
        range.SelectionChanged += async (_, _) =>
        {
            if (loading) return;
            monthRange = false;
            await RunAsync(QueryAsync);
        };
        history.Children.Add(Label(UiText.PeriodDefinition));
        history.Children.Add(period);
        period.SelectionChanged += (_, _) => { if (!loading) Draw(); };
        history.Children.Add(chart);
        history.Children.Add(Label(UiText.HistoryLegend));
        var details = new Border
        {
            Padding = new Thickness(16), CornerRadius = new CornerRadius(10),
            Child = detail, Margin = new Thickness(0, 8, 0, 12)
        };
        details.SetResourceReference(Border.BackgroundProperty, "SoftBrush");
        history.Children.Add(details);
        history.Children.Add(Label(UiText.HistoryNotes));
        var buttons = new WrapPanel();
        buttons.Children.Add(ActionButton(UiText.OpenHistoryFolder, () => OpenAsync(HistoryLocation.Folder)));
        buttons.Children.Add(ActionButton(UiText.ShowHistoryFile, () => OpenAsync(HistoryLocation.SummaryFile)));
        history.Children.Add(buttons);
    }

    private void BuildStorage()
    {
        storage.Children.Add(Label(UiText.HistoryLocation, 18, true));
        storage.Children.Add(status);
        folder.FontFamily = new FontFamily("Consolas");
        folder.TextWrapping = TextWrapping.Wrap;
        folder.Margin = new Thickness(0, 12, 0, 12);
        storage.Children.Add(folder);
        var buttons = new WrapPanel();
        choose.Click += async (_, _) => await RunAsync(ChangeFolderAsync);
        translations.Add(() => choose.Content = localizer.Text(UiText.ChooseFolder));
        buttons.Children.Add(choose);
        buttons.Children.Add(ActionButton(UiText.OpenHistoryFolder, () => OpenAsync(HistoryLocation.Folder)));
        storage.Children.Add(buttons);
        storage.Children.Add(Label(UiText.StorageNotes));
        storage.Children.Add(Label(UiText.ArchiveHeading, 18, true));
        storage.Children.Add(Label(UiText.ArchiveNotes));
        storage.Children.Add(archiveStatus);
        var archive = new WrapPanel();
        archive.Children.Add(ActionButton(UiText.CreateVerifyArchive, async () =>
        {
            var selected = SelectedMonth();
            if (MessageBox.Show(this, localizer.Format(UiText.ArchiveConfirmation,
                selected.ToString("yyyy-MM", CultureInfo.InvariantCulture)), localizer.Text(UiText.ArchiveConfirmationTitle),
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            var response = await RequestAsync(new(HistoryAction.Archive, Month: selected));
            archiveStatus.Text = localizer.Format(UiText.ArchiveCreatedVerified, response.File);
        }));
        archive.Children.Add(ActionButton(UiText.VerifyArchive, async () =>
        {
            var response = await RequestAsync(new(HistoryAction.VerifyArchive, Month: SelectedMonth()));
            archiveStatus.Text = localizer.Format(UiText.ArchiveVerified, response.File);
        }));
        archive.Children.Add(ActionButton(UiText.OpenArchiveFolder, () => OpenAsync(HistoryLocation.ArchiveFolder)));
        archive.Children.Add(ActionButton(UiText.ShowArchiveFile, () => OpenAsync(HistoryLocation.ArchiveFile)));
        storage.Children.Add(archive);
        storage.Children.Add(Label(UiText.OpenDoesNotVerify));
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || stopping.IsCancellationRequested) return;
        IsBusy = true; history.IsEnabled = false; storage.IsEnabled = false; error.Text = "";
        try { await action(); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) when (ex is UsageException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            error.Text = ex is UsageException usage ? localizer.Error(usage.SafeCode)
                : localizer.Format(UiText.OperationFailed, ex.GetType().Name);
        }
        finally { IsBusy = false; history.IsEnabled = true; storage.IsEnabled = true; }
    }

    private async Task<HistoryResponse> RequestAsync(HistoryRequest request)
    {
        var response = await send(request, stopping.Token);
        if (response.Status is { } saved)
            SetStorageStatus(saved);
        if (!response.Success)
            throw new UsageException(FailureKind.Storage, response.Error ?? "history_response_missing");
        if (response.Options is { } value)
        {
            folder.Text = value.Folder;
        }
        return response;
    }

    private async Task LoadAsync()
    {
        var response = await RequestAsync(new(HistoryAction.Catalog));
        catalog = response.Catalog ?? throw new UsageException(FailureKind.Schema, "history_catalog_missing");
        loading = true;
        try
        {
            if (provider.SelectedItem is null) provider.SelectedIndex = 0;
            PopulateAccounts();
        }
        finally { loading = false; }
        await QueryAsync();
    }

    private void PopulateAccounts()
    {
        bool previous = loading; loading = true;
        var selected = (account.SelectedItem as ComboBoxItem)?.Tag as AccountKey;
        account.Items.Clear();
        foreach (var key in catalog.Where(c => c.Key.Account.Provider == (Provider)provider.SelectedItem)
            .Select(c => c.Key.Account).Distinct())
            account.Items.Add(new ComboBoxItem { Content = key.Id[..8], Tag = key });
        account.SelectedItem = account.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
        if (account.SelectedItem is null && account.Items.Count > 0) account.SelectedIndex = 0;
        PopulateBuckets();
        loading = previous;
        if (!previous) _ = RunAsync(QueryAsync);
    }

    private void PopulateBuckets()
    {
        bool previous = loading; loading = true;
        var selected = (bucket.SelectedItem as ComboBoxItem)?.Tag as QuotaKey;
        bucket.Items.Clear();
        var selectedAccount = (account.SelectedItem as ComboBoxItem)?.Tag as AccountKey;
        foreach (var item in catalog.Where(c => c.Key.Account == selectedAccount))
            bucket.Items.Add(new ComboBoxItem { Content = item.Key.Bucket, Tag = item.Key });
        bucket.SelectedItem = bucket.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
        if (bucket.SelectedItem is null && bucket.Items.Count > 0) bucket.SelectedIndex = 0;
        loading = previous;
        if (!previous) _ = RunAsync(QueryAsync);
    }

    private DateOnly SelectedMonth()
    {
        if (!DateOnly.TryParseExact(month.Text.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var selected))
            throw new UsageException(FailureKind.Schema, "history_month_invalid");
        return selected;
    }

    private async Task QueryAsync()
    {
        var selected = (period.SelectedItem as ComboBoxItem)?.Tag as string;
        days = [];
        chart.Children.Clear();
        detail.Text = localizer.Text(UiText.HistoryLoading);
        loading = true;
        period.Items.Clear();
        loading = false;
        if ((bucket.SelectedItem as ComboBoxItem)?.Tag is not QuotaKey key)
        {
            days = []; Draw(); detail.Text = localizer.Text(UiText.NoRecordedQuota); return;
        }
        DateOnly from, through;
        if (monthRange)
        {
            from = SelectedMonth(); through = from.AddMonths(1).AddDays(-1);
        }
        else
        {
            through = JapaneseCalendar.Date(DateTimeOffset.UtcNow);
            from = through.AddDays(1 - (int)((ComboBoxItem)range.SelectedItem).Tag);
        }
        var response = await RequestAsync(new(HistoryAction.Query, Query: new(from, through, key)));
        days = response.Days ?? throw new UsageException(FailureKind.Schema, "history_response_missing");
        loading = true;
        try
        {
            period.Items.Clear();
            foreach (var slice in days.SelectMany(d => d.Slices).GroupBy(s => s.PeriodId).Select(g => g.Last()))
                period.Items.Add(new ComboBoxItem
                {
                    Content = PeriodTitle(slice),
                    Tag = slice.PeriodId
                });
            period.SelectedItem = period.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
            if (period.SelectedItem is null && period.Items.Count > 0) period.SelectedIndex = period.Items.Count - 1;
        }
        finally { loading = false; }
        Draw();
    }

    private string PeriodTitle(DailyHistorySlice slice) =>
        $"{slice.Plan ?? localizer.Text(UiText.PlanUnknown)} · {localizer.Unit(slice.Unit)} · "
        + $"{slice.Window.StartsAt?.ToOffset(JapaneseCalendar.Offset).ToString("g", localizer.Culture)} – "
        + $"{slice.Window.ResetsAt?.ToOffset(JapaneseCalendar.Offset).ToString("g", localizer.Culture)}";

    private Brush Hatch()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brush("SoftBrush"), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("BorderBrush"), 2),
            Geometry.Parse("M 0,8 L 8,0 M -4,4 L 4,-4 M 4,12 L 12,4")));
        return new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
    }

    private void Draw()
    {
        chart.Children.Clear();
        if (days.Count == 0) return;
        var selected = (period.SelectedItem as ComboBoxItem)?.Tag as string;
        var slices = days.Select(d => d.Slices.SingleOrDefault(s => s.PeriodId == selected)).ToArray();
        decimal maximum = Math.Max(1, slices.Max(s => Math.Max(s?.Observed ?? 0, s?.Guidance ?? 0))) * 1.2m;
        var width = Math.Max(680, chart.ActualWidth) - 56;
        const double height = 208;
        double cell = width / days.Count;
        var unit = slices.FirstOrDefault(s => s is not null)?.Unit ?? UsageUnit.Unknown;
        for (int step = 0; step <= 2; step++)
        {
            var y = height - height * step / 2;
            var line = new Line { X1 = 44, X2 = width + 44, Y1 = y, Y2 = y,
                Stroke = Brush("BorderBrush"), StrokeThickness = 1 };
            chart.Children.Add(line);
            var label = Label((maximum * step / 2).ToString("0.#", localizer.Culture)
                + (step == 2 ? " " + localizer.Unit(unit) : ""), 10);
            Canvas.SetTop(label, y - 12); chart.Children.Add(label);
        }
        for (int i = 0; i < days.Count; i++)
        {
            var slice = slices[i];
            bool partial = IsPartial(days[i].Date, slice);
            var measured = slice?.Observed;
            double barHeight = measured is { } value ? Math.Max(2, height * (double)(value / maximum)) : height;
            var rect = new Rectangle { Width = Math.Max(2, cell - 6), Height = barHeight,
                Fill = measured is null || partial ? Hatch() : Brush("AccentBrush"),
                Stroke = Brush(measured is null ? "BorderBrush" : "AccentBrush"), StrokeThickness = 1,
                Opacity = measured is null ? 0.5 : 1, Focusable = true };
            Canvas.SetLeft(rect, 44 + cell * i + 3); Canvas.SetTop(rect, height - barHeight);
            var day = days[i].Date;
            rect.ToolTip = Detail(day, slice, partial);
            AutomationProperties.SetName(rect, Detail(day, slice, partial));
            rect.MouseLeftButtonDown += (_, _) => detail.Text = Detail(day, slice, partial);
            rect.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space) { detail.Text = Detail(day, slice, partial); e.Handled = true; }
            };
            chart.Children.Add(rect);
            if (slice?.Guidance is { } guidance)
            {
                double y = height - height * (double)(guidance / maximum);
                chart.Children.Add(new Line { X1 = 44 + cell * i, X2 = 44 + cell * (i + 1), Y1 = y, Y2 = y,
                    Stroke = Brush("WarningBrush"), StrokeThickness = 2, StrokeDashArray = [3, 2] });
            }
            if (days.Count <= 7 || i % (days.Count <= 31 ? 5 : 14) == 0)
            {
                var label = Label(day.ToString("M/d", CultureInfo.InvariantCulture), 10);
                Canvas.SetLeft(label, 44 + cell * i); Canvas.SetTop(label, height);
                chart.Children.Add(label);
            }
        }
        detail.Text = Detail(days[^1].Date, slices[^1], IsPartial(days[^1].Date, slices[^1]));
    }

    private static bool IsPartial(DateOnly date, DailyHistorySlice? slice)
    {
        if (slice?.Coverage != Coverage.CompleteToObservation) return true;
        var dayEnd = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), JapaneseCalendar.Offset);
        var end = slice.Window.ResetsAt is { } reset && reset < dayEnd ? reset : dayEnd;
        return date < JapaneseCalendar.Date(DateTimeOffset.UtcNow) && slice.ObservedThrough < end;
    }

    private string Detail(DateOnly date, DailyHistorySlice? slice, bool partial) =>
        localizer.Format(UiText.DayDetail, date.ToString("d", localizer.Culture),
            localizer.Text(slice?.Observed is null ? UiText.Unmeasured : partial ? UiText.Partial : UiText.CompleteToObservation),
            slice?.Observed?.ToString("0.##", localizer.Culture) ?? "—", localizer.Unit(slice?.Unit ?? UsageUnit.Unknown),
            slice?.Guidance?.ToString("0.##", localizer.Culture) ?? "—",
            slice?.GuidancePercent?.ToString("0.#", localizer.Culture) ?? "—",
            slice?.ObservedThrough?.ToOffset(JapaneseCalendar.Offset).ToString("g", localizer.Culture) ?? localizer.Text(UiText.NoRecord))
        + (slice is null ? "" : "\n" + localizer.Text(UiText.Changes) + ": " + string.Join(", ", slice.Events)
            + (slice.UnallocatedBoundary ? " · " + localizer.Text(UiText.DayBoundaryUnallocated) : "")
            + (slice.HasGaps ? " · " + localizer.Text(UiText.ObservationGap) : ""));

    private async Task OpenAsync(HistoryLocation location)
    {
        var selected = location == HistoryLocation.SummaryFile && !monthRange && days.Count > 0
            ? days[^1].Date : SelectedMonth();
        await RequestAsync(new(HistoryAction.OpenLocation, Month: selected, Location: location));
    }

    private async Task ChangeFolderAsync()
    {
        IsLocationEditing = true;
        try
        {
            var picker = new OpenFolderDialog { Title = localizer.Text(UiText.FolderPickerTitle), Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            var response = await RequestAsync(new(HistoryAction.PreviewLocation, Folder: picker.FolderName));
            var preview = response.Migration ?? throw new UsageException(FailureKind.Schema, "history_preview_missing");
            if (!preview.Writable || preview.Error is not null)
                throw new UsageException(FailureKind.Storage, preview.Error ?? "history_folder_not_writable");
            var text = localizer.Format(UiText.MigrationPreview, preview.Source, preview.Destination,
                preview.Files.Count, preview.CopyBytes, preview.AvailableBytes is { } free
                    ? free.ToString("N0", localizer.Culture) + " bytes" : localizer.Text(UiText.FreeSpaceUnknown));
            if (MessageBox.Show(this, text, localizer.Text(UiText.MigrationTitle), MessageBoxButton.OKCancel,
                MessageBoxImage.Information) != MessageBoxResult.OK) return;
            await RequestAsync(new(HistoryAction.ChangeLocation, PreviewId: response.PreviewId));
            error.Text = localizer.Text(UiText.MigrationCompleted);
        }
        finally { IsLocationEditing = false; }
    }
}
