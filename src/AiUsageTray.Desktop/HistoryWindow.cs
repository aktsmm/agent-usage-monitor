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
using System.Windows.Threading;
using AiUsageTray.Core;
using Microsoft.Win32;
using JapaneseCalendar = AiUsageTray.Core.JapaneseCalendar;

namespace AiUsageTray.Desktop;

public sealed class HistoryWindow : Window
{
    private readonly Func<HistoryRequest, CancellationToken, Task<HistoryResponse>> send;
    private UiLocalizer localizer;
    private readonly Func<AccountKey, string?>? accountName;
    private readonly Func<bool> showAllBuckets;
    private bool? loadedShowAll;
    private bool reloadAfterBusy;
    private readonly List<Action> translations = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly ComboBox provider = new(), account = new(), bucket = new(), range = new(), period = new();
    private readonly TextBox month = new();
    private readonly Canvas chart = CreateChart();
    private readonly ListBox overview = new() { MaxHeight = 250, MinHeight = 80, Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBlock overviewStatus = new(), graphStatus = new(), overviewHeader = new();
    private readonly Expander overviewExpander = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly StackPanel overviewBody = new();
    private readonly TextBlock detail = new(), error = new(), status = new(), folder = new(), archiveStatus = new();
    private readonly StackPanel history = new(), storage = new();
    private readonly Button choose = new();
    private readonly Button showGraph = new() { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4), IsEnabled = false };
    private List<QuotaKey> catalog = [];
    private List<HistoryDay> days = [];
    private List<HistoryOverviewItem> overviewItems = [];
    private sealed record PeriodSelection(string PeriodId, string? Plan, UsageUnit Unit, QuotaWindow Window);
    private static PeriodSelection PeriodOf(DailyHistorySlice slice) =>
        new(slice.PeriodId, slice.Plan, slice.Unit, slice.Window);
    private static PeriodSelection? PeriodOf(HistoryOverviewItem item) => item.PeriodId is not null && item.Window is not null
        ? new(item.PeriodId, item.Plan, item.Unit, item.Window) : null;
    private PeriodSelection? requestedPeriod;
    private DateOnly? selectedDay;
    private string? overviewFailure;
    private bool loading;
    private bool monthRange;
    private HistoryStorageStatus? storageStatus;
    public bool IsBusy { get; private set; }
    public bool IsLocationEditing { get; private set; }

    private static Canvas CreateChart()
    {
        var canvas = new HistoryChartCanvas { Height = 252 };
        // One Tab stop for the whole graph; arrow keys move between days.
        KeyboardNavigation.SetTabNavigation(canvas, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(canvas, KeyboardNavigationMode.Contained);
        return canvas;
    }
    public HistoryWindow(Func<HistoryRequest, CancellationToken, Task<HistoryResponse>> send,
        UiLocalizer? localizer = null, Func<AccountKey, string?>? accountName = null, Func<bool>? showAllBuckets = null)
    {
        this.send = send;
        this.accountName = accountName;
        this.showAllBuckets = showAllBuckets ?? (() => false);
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
        overviewStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        graphStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        overviewStatus.TextWrapping = TextWrapping.Wrap;
        graphStatus.TextWrapping = TextWrapping.Wrap;
        overview.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        overview.SetResourceReference(Control.BackgroundProperty, "SoftBrush");
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
        Activated += async (_, _) =>
        {
            Draw();
            // Changing the Copilot bucket setting while history is open takes effect when the window is activated again.
            if (loadedShowAll is { } loaded && loaded != this.showAllBuckets())
            {
                if (IsBusy) reloadAfterBusy = true;
                else await RunAsync(LoadAsync);
            }
        };
    }

    public void SetLanguage(UiLocalizer value)
    {
        localizer = value;
        foreach (var apply in translations) apply();
        foreach (ComboBoxItem item in range.Items)
            item.Content = localizer.Format(UiText.RangeDays, item.Tag);
        foreach (ComboBoxItem item in bucket.Items)
            if (item.Tag is QuotaKey key) item.Content = localizer.Bucket(key);
        foreach (ComboBoxItem item in period.Items)
            if (days.SelectMany(d => d.Slices).FirstOrDefault(s => PeriodOf(s) == item.Tag as PeriodSelection) is { } slice)
                item.Content = PeriodTitle(slice);
        if (storageStatus is { } status) SetStorageStatus(status);
        RenderOverview();
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
        var controls = new WrapPanel();
        foreach (int value in new[] { 7, 30, 90 }) range.Items.Add(new ComboBoxItem { Tag = value });
        range.SelectedIndex = 1;
        Field(controls, UiText.Range, range);
        month.Text = DailyHistory.Month(JapaneseCalendar.Date(DateTimeOffset.UtcNow));
        Field(controls, UiText.MonthInput, month);
        controls.Children.Add(ActionButton(UiText.ShowMonth, async () =>
        {
            monthRange = true;
            await LoadOverviewAsync();
            await ShowOverviewSelectionAsync();
        }));
        controls.Children.Add(ActionButton(UiText.Reload, LoadAsync));
        history.Children.Add(controls);
        range.SelectionChanged += async (_, _) =>
        {
            if (loading) return;
            monthRange = false;
            await RunAsync(async () => { await LoadOverviewAsync(); await ShowOverviewSelectionAsync(); });
        };
        overviewExpander.IsExpanded = false;
        overviewExpander.Header = overviewHeader;
        overviewHeader.FontSize = 17;
        overviewHeader.FontWeight = FontWeights.SemiBold;
        overviewHeader.TextWrapping = TextWrapping.Wrap;
        overviewHeader.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var hint = Label(UiText.HistoryOverviewHint);
        hint.TextWrapping = TextWrapping.Wrap;
        overviewBody.Children.Add(hint);
        overviewBody.Children.Add(overview);
        overviewExpander.Content = overviewBody;
        history.Children.Add(overviewExpander);
        history.Children.Add(overviewStatus);
        overview.SelectionChanged += async (_, _) =>
        {
            UpdateOverviewSelection();
            UpdateOverviewHeader();
            if (!loading && (overview.SelectedItem as ListBoxItem)?.Tag is HistoryOverviewItem item)
                await RunAsync(() => SelectOverviewAsync(item));
        };
        translations.Add(() =>
        {
            showGraph.Content = localizer.Text(UiText.HistoryShowGraph);
            AutomationProperties.SetName(showGraph, localizer.Text(UiText.HistoryShowGraph));
        });
        showGraph.SetResourceReference(Control.BackgroundProperty, "SoftBrush");
        showGraph.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        showGraph.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        showGraph.Click += async (_, _) => await ShowGraphAsync();
        history.Children.Add(showGraph);
        var filters = new WrapPanel();
        foreach (var value in Enum.GetValues<Provider>()) provider.Items.Add(value);
        Field(filters, UiText.Service, provider);
        Field(filters, UiText.AccountHash, account);
        Field(filters, UiText.Quota, bucket);
        history.Children.Add(filters);
        provider.SelectionChanged += (_, _) => { if (!loading) PopulateAccounts(); };
        account.SelectionChanged += (_, _) => { if (!loading) PopulateBuckets(); };
        bucket.SelectionChanged += async (_, _) => { if (!loading) await RunAsync(QueryAsync); };
        history.Children.Add(Label(UiText.PeriodDefinition));
        history.Children.Add(period);
        var units = Label(UiText.PeriodUnitsExplanation);
        units.TextWrapping = TextWrapping.Wrap;
        history.Children.Add(units);
        period.SelectionChanged += (_, _) => { if (!loading) { Draw(); SyncOverviewSelection(); } };
        translations.Add(() =>
        {
            AutomationProperties.SetName(chart, localizer.Text(UiText.HistoryGraphName));
            AutomationProperties.SetHelpText(chart, localizer.Text(UiText.HistoryGraphHelp));
        });
        history.Children.Add(chart);
        history.Children.Add(graphStatus);
        var legend = Label(UiText.HistoryLegend);
        legend.TextWrapping = TextWrapping.Wrap;
        history.Children.Add(legend);
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

    private async Task ShowGraphAsync()
    {
        var target = chart.Children.OfType<Button>().ElementAtOrDefault(days.FindIndex(d => d.Date == selectedDay));
        if (target is null) return;
        chart.BringIntoView();
        if (!target.Focus() || !IsActive) return;
        bool interrupted = false;
        void OnDeactivated(object? sender, EventArgs e) => interrupted = true;
        void OnUserInput(object sender, InputEventArgs e) => interrupted = true;
        void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!ReferenceEquals(e.NewFocus, target)) interrupted = true;
        }
        Deactivated += OnDeactivated;
        Keyboard.AddGotKeyboardFocusHandler(this, OnFocusChanged);
        PreviewKeyDown += OnUserInput;
        PreviewMouseDown += OnUserInput;
        PreviewMouseWheel += OnUserInput;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!interrupted && IsActive && !stopping.IsCancellationRequested && target.IsLoaded &&
                    ReferenceEquals(Keyboard.FocusedElement, target))
                    chart.BringIntoView();
            }, DispatcherPriority.ContextIdle);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally
        {
            Deactivated -= OnDeactivated;
            Keyboard.RemoveGotKeyboardFocusHandler(this, OnFocusChanged);
            PreviewKeyDown -= OnUserInput;
            PreviewMouseDown -= OnUserInput;
            PreviewMouseWheel -= OnUserInput;
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || stopping.IsCancellationRequested) return;
        var focused = Keyboard.FocusedElement as FrameworkElement;
        var restoreFocus = IsActive && focused is not null &&
            (history.IsKeyboardFocusWithin || storage.IsKeyboardFocusWithin);
        bool deactivated = false;
        bool focusChanged = false;
        bool userInput = false;
        IInputElement? automaticFocus = null;
        void OnDeactivated(object? sender, EventArgs e) => deactivated = true;
        void OnUserInput(object sender, InputEventArgs e) => userInput = true;
        void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (automaticFocus is null && ReferenceEquals(e.OldFocus, focused) &&
                e.NewFocus is DependencyObject ancestor && focused is not null && IsVisualAncestor(ancestor, focused))
                automaticFocus = e.NewFocus;
            else if (!ReferenceEquals(e.NewFocus, focused))
                focusChanged = true;
        }
        if (restoreFocus)
        {
            Deactivated += OnDeactivated;
            Keyboard.AddGotKeyboardFocusHandler(this, OnFocusChanged);
            PreviewKeyDown += OnUserInput;
            PreviewMouseDown += OnUserInput;
            PreviewMouseWheel += OnUserInput;
        }
        IsBusy = true; history.IsEnabled = false; storage.IsEnabled = false; error.Text = "";
        try { await action(); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) when (ex is UsageException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            error.Text = ex is UsageException usage ? localizer.Error(usage.SafeCode)
                : localizer.Format(UiText.OperationFailed, ex.GetType().Name);
        }
        finally
        {
            IsBusy = false; history.IsEnabled = true; storage.IsEnabled = true;
            if (restoreFocus)
            {
                try
                {
                    if (!deactivated && !focusChanged && !userInput && IsActive && !stopping.IsCancellationRequested &&
                        automaticFocus is not null && ReferenceEquals(Keyboard.FocusedElement, automaticFocus) &&
                        focused is { IsLoaded: true, IsVisible: true, IsEnabled: true })
                        focused.Focus();
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
                finally
                {
                    Deactivated -= OnDeactivated;
                    Keyboard.RemoveGotKeyboardFocusHandler(this, OnFocusChanged);
                    PreviewKeyDown -= OnUserInput;
                    PreviewMouseDown -= OnUserInput;
                    PreviewMouseWheel -= OnUserInput;
                }
            }
        }
        // A setting change that arrived while this operation was running is applied once it has finished.
        if (reloadAfterBusy)
        {
            reloadAfterBusy = false;
            if (loadedShowAll is { } loaded && loaded != showAllBuckets()) await RunAsync(LoadAsync);
        }
    }

    private static bool IsVisualAncestor(DependencyObject candidate, DependencyObject target)
    {
        for (var parent = VisualTreeHelper.GetParent(target); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (ReferenceEquals(candidate, parent)) return true;
        return false;
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
        await LoadOverviewAsync();
        await ShowOverviewSelectionAsync();
    }

    private (DateOnly From, DateOnly Through) QueryRange()
    {
        if (monthRange)
        {
            var from = SelectedMonth();
            return (from, from.AddDays(DateTime.DaysInMonth(from.Year, from.Month) - 1));
        }
        var through = JapaneseCalendar.Date(DateTimeOffset.UtcNow);
        return (through.AddDays(1 - (int)((ComboBoxItem)range.SelectedItem).Tag), through);
    }

    private async Task LoadOverviewAsync()
    {
        var selected = (overview.SelectedItem as ListBoxItem)?.Tag as HistoryOverviewItem;
        var selectedKey = selected?.Key ?? (bucket.SelectedItem as ComboBoxItem)?.Tag as QuotaKey;
        var selectedPeriod = selected is not null ? PeriodOf(selected)
            : (period.SelectedItem as ComboBoxItem)?.Tag as PeriodSelection;
        overviewItems = [];
        overviewFailure = null;
        RenderOverview();
        overviewStatus.Text = localizer.Text(UiText.HistoryLoading);
        var completed = false;
        try
        {
            var (from, through) = QueryRange();
            int offset = 0;
            string? snapshot = null;
            try
            {
                do
                {
                    var response = await RequestAsync(new(HistoryAction.Overview, From: from, Through: through,
                        Offset: offset, Snapshot: snapshot));
                    var page = response.Overview ?? throw new UsageException(FailureKind.Schema, "history_response_missing");
                    if (string.IsNullOrWhiteSpace(page.Snapshot) || page.Items is null)
                        throw new UsageException(FailureKind.Schema, "history_response_missing");
                    if (snapshot is not null && snapshot != page.Snapshot)
                        throw new UsageException(FailureKind.Storage, "history_overview_changed");
                    overviewItems.AddRange(page.Items);
                    snapshot = page.Snapshot;
                    if (page.NextOffset is not { } next) break;
                    if (next <= offset || next - offset != page.Items.Count)
                        throw new UsageException(FailureKind.Schema, "history_response_missing");
                    offset = next;
                } while (true);
                catalog = overviewItems.Select(i => i.Key).Distinct().ToList();
            }
            catch (UsageException ex) when (ex.SafeCode == "ipc_message_too_large")
            {
                overviewItems.Clear();
                var response = await RequestAsync(new(HistoryAction.Catalog));
                catalog = (response.Catalog ?? throw new UsageException(FailureKind.Schema, "history_catalog_missing"))
                    .Select(item => item.Key).Distinct().ToList();
                overviewFailure = ex.SafeCode;
            }
            // Only the display is narrowed: records of the hidden Copilot buckets stay in history.
            var showAll = showAllBuckets();
            loadedShowAll = showAll;
            overviewItems = QuotaVisibility.PrimaryBuckets(overviewItems, item => item.Key, showAll).ToList();
            catalog = QuotaVisibility.PrimaryBuckets(catalog, key => key, showAll).ToList();
            loading = true;
            try
            {
                if (provider.SelectedItem is null) provider.SelectedIndex = 0;
                PopulateAccounts();
            }
            finally { loading = false; }
            RenderOverview();
            foreach (var filter in new[] { provider, account, bucket, period }) filter.IsEnabled = true;
            var rows = overview.Items.OfType<ListBoxItem>().ToArray();
            var row = rows.FirstOrDefault(i => i.Tag is HistoryOverviewItem candidate &&
                    selectedKey is not null && candidate.Key == selectedKey && PeriodOf(candidate) == selectedPeriod)
                ?? rows.FirstOrDefault(i => i.Tag is HistoryOverviewItem candidate && candidate.Key == selectedKey)
                // The selected bucket was filtered out: stay on the same account and prefer its premium bucket.
                ?? rows.Where(i => i.Tag is HistoryOverviewItem candidate && selectedKey is not null &&
                        candidate.Key.Account == selectedKey.Account)
                    .OrderBy(i => QuotaVisibility.BucketPreference(((HistoryOverviewItem)i.Tag).Key)).FirstOrDefault()
                // Nothing was selected before: open the first quota, on its period with the most observed days (not a stray one).
                ?? rows.Where(i => rows.Length > 0 && (i.Tag as HistoryOverviewItem)?.Key == (rows[0].Tag as HistoryOverviewItem)?.Key)
                    .OrderByDescending(i => (i.Tag as HistoryOverviewItem)?.MeasuredDays ?? 0).FirstOrDefault();
            loading = true;
            try { overview.SelectedItem = row; }
            finally { loading = false; }
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                overviewItems.Clear();
                overviewFailure = null;
                loading = true;
                try
                {
                    catalog.Clear();
                    overview.Items.Clear();
                    account.Items.Clear();
                    bucket.Items.Clear();
                    period.Items.Clear();
                    requestedPeriod = null;
                    foreach (var filter in new[] { provider, account, bucket, period }) filter.IsEnabled = false;
                }
                finally { loading = false; }
                overviewStatus.Text = "";
                days.Clear();
                chart.Children.Clear();
                showGraph.IsEnabled = false;
                graphStatus.Text = "";
                detail.Text = "";
            }
        }
    }

    private Task ShowOverviewSelectionAsync() =>
        (overview.SelectedItem as ListBoxItem)?.Tag is HistoryOverviewItem item
            ? SelectOverviewAsync(item) : QueryAsync();

    private void RenderOverview()
    {
        bool previous = loading;
        loading = true;
        try
        {
            var selected = (overview.SelectedItem as ListBoxItem)?.Tag as HistoryOverviewItem;
            overview.Items.Clear();
            foreach (var item in overviewItems.OrderByDescending(i => i.Observed > 0)
                .ThenBy(i => i.Key.Account.Provider).ThenBy(i => i.Key.Bucket))
            {
                var name = accountName?.Invoke(item.Key.Account) ?? item.Key.Account.Id[..8];
                var periodText = item.Window is null ? "" : $" · {item.Plan ?? localizer.Text(UiText.PlanUnknown)}"
                    + $" · {item.Window.StartsAt?.ToOffset(JapaneseCalendar.Offset).ToString("g", localizer.Culture)}"
                    + $" – {item.Window.ResetsAt?.ToOffset(JapaneseCalendar.Offset).ToString("g", localizer.Culture)}"
                    + $" · {Denominator(item.PeriodId)}";
                var amount = item.Observed is { } value
                    ? localizer.Format(UiText.HistoryOverviewMeasured, localizer.Amount(value),
                        localizer.Unit(item.Unit), item.MeasuredDays)
                    : localizer.Text(UiText.HistoryOverviewUnmeasured);
                var content = $"{item.Key.Account.Provider} · {name} · {localizer.Bucket(item.Key)}{periodText}\n{amount}";
                if (item.Observed is not null && (item.PartialDays > 0 || item.UnmeasuredDays > 0))
                    content += " · " + localizer.Text(UiText.HistoryOverviewPartial);
                overview.Items.Add(new ListBoxItem { Content = content, Tag = item, Padding = new Thickness(8) });
            }
            overview.SelectedItem = overview.Items.OfType<ListBoxItem>()
                .FirstOrDefault(i => Equals(i.Tag, selected));
            UpdateOverviewSelection();
            overviewStatus.Text = overviewFailure is not null
                ? localizer.Error(overviewFailure) + "\n" + localizer.Text(UiText.HistoryOverviewFallback)
                : overviewItems.Count == 0 ? localizer.Text(UiText.HistoryOverviewEmpty) : "";
            UpdateOverviewHeader();
        }
        finally { loading = previous; }
    }

    // The list is collapsed by default, so its header names the selected item and the number of items.
    private void UpdateOverviewHeader()
    {
        var text = localizer.Text(UiText.HistoryOverview) + " · " + localizer.Format(UiText.HistoryOverviewCount, overview.Items.Count);
        if ((overview.SelectedItem as ListBoxItem)?.Tag is HistoryOverviewItem item)
            text += " · " + localizer.Format(UiText.HistoryOverviewSelected,
                $"{item.Key.Account.Provider} · {accountName?.Invoke(item.Key.Account) ?? item.Key.Account.Id[..8]} · {localizer.Bucket(item.Key)}");
        overviewHeader.Text = text;
        AutomationProperties.SetName(overviewExpander, text);
    }

    private void UpdateOverviewSelection()
    {
        foreach (var row in overview.Items.OfType<ListBoxItem>())
        {
            row.BorderThickness = new Thickness(row.IsSelected ? 2 : 1);
            row.SetResourceReference(Control.BorderBrushProperty, row.IsSelected ? "AccentBrush" : "BorderBrush");
        }
    }

    private async Task SelectOverviewAsync(HistoryOverviewItem item)
    {
        loading = true;
        try
        {
            provider.SelectedItem = item.Key.Account.Provider;
            PopulateAccounts();
            account.SelectedItem = account.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => Equals(i.Tag, item.Key.Account));
            PopulateBuckets();
            bucket.SelectedItem = bucket.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => Equals(i.Tag, item.Key));
            requestedPeriod = PeriodOf(item);
        }
        finally { loading = false; }
        await QueryAsync();
    }

    private void PopulateAccounts()
    {
        bool previous = loading; loading = true;
        var selected = (account.SelectedItem as ComboBoxItem)?.Tag as AccountKey;
        account.Items.Clear();
        foreach (var key in catalog.Where(c => c.Account.Provider == (Provider)provider.SelectedItem)
            .Select(c => c.Account).Distinct())
            account.Items.Add(new ComboBoxItem { Content = accountName?.Invoke(key) ?? key.Id[..8], Tag = key });
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
        foreach (var item in catalog.Where(c => c.Account == selectedAccount))
            bucket.Items.Add(new ComboBoxItem { Content = localizer.Bucket(item), Tag = item });
        var choices = bucket.Items.OfType<ComboBoxItem>().ToArray();
        // A quota key belongs to one account, so switching accounts keeps the same bucket by name and otherwise prefers premium.
        bucket.SelectedItem = choices.FirstOrDefault(i => Equals(i.Tag, selected))
            ?? choices.FirstOrDefault(i => selected is not null && (i.Tag as QuotaKey)?.Bucket == selected.Bucket)
            ?? choices.FirstOrDefault(i => i.Tag is QuotaKey key && QuotaVisibility.BucketPreference(key) == 0);
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
        var selected = requestedPeriod ?? (period.SelectedItem as ComboBoxItem)?.Tag as PeriodSelection;
        requestedPeriod = null;
        days = [];
        chart.Children.Clear();
        showGraph.IsEnabled = false;
        detail.Text = localizer.Text(UiText.HistoryLoading);
        loading = true;
        period.Items.Clear();
        loading = false;
        if ((bucket.SelectedItem as ComboBoxItem)?.Tag is not QuotaKey key)
        {
            days = []; Draw(); SyncOverviewSelection();
            detail.Text = localizer.Text(UiText.NoRecordedQuota); return;
        }
        var completed = false;
        try
        {
            var (from, through) = QueryRange();
            var fragments = new List<HistoryDay>();
            var query = new HistoryQuery(from, through, key);
            int offset = 0;
            string? snapshot = null;
            do
            {
                var response = await RequestAsync(new(HistoryAction.QueryPage, Query: query,
                    Offset: offset, Snapshot: snapshot));
                var page = response.DaysPage ?? throw new UsageException(FailureKind.Schema, "history_response_missing");
                if (string.IsNullOrWhiteSpace(page.Snapshot) || page.Days is null)
                    throw new UsageException(FailureKind.Schema, "history_response_missing");
                if (snapshot is not null && snapshot != page.Snapshot)
                    throw new UsageException(FailureKind.Storage, "history_overview_changed");
                fragments.AddRange(page.Days);
                snapshot = page.Snapshot;
                if (page.NextOffset is not { } next) break;
                if (next <= offset || next - offset != page.Days.Count)
                    throw new UsageException(FailureKind.Schema, "history_response_missing");
                offset = next;
            } while (true);
            days = fragments.GroupBy(day => day.Date).OrderBy(g => g.Key)
                .Select(g => new HistoryDay(g.Key, g.SelectMany(day => day.Slices).ToArray())).ToList();
            loading = true;
            try
            {
                period.Items.Clear();
                // Most observed days first, so the period that holds the data is the default (a few stray records of
                // another unit never come first).
                foreach (var slice in days.SelectMany(d => d.Slices).GroupBy(PeriodOf).Select(g => g.Last())
                    .OrderByDescending(s => MeasuredDays(PeriodOf(s))).ThenByDescending(s => s.Window.StartsAt))
                    period.Items.Add(new ComboBoxItem
                    {
                        Content = PeriodTitle(slice),
                        Tag = PeriodOf(slice)
                    });
                period.SelectedItem = period.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
                if (period.SelectedItem is null && period.Items.Count > 0) period.SelectedIndex = 0;
            }
            finally { loading = false; }
            Draw();
            SyncOverviewSelection();
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                days.Clear();
                chart.Children.Clear();
                showGraph.IsEnabled = false;
                graphStatus.Text = "";
                detail.Text = "";
            }
        }
    }

    private void SyncOverviewSelection()
    {
        var key = (bucket.SelectedItem as ComboBoxItem)?.Tag as QuotaKey;
        loading = true;
        try
        {
            var selectedPeriod = (period.SelectedItem as ComboBoxItem)?.Tag as PeriodSelection;
            overview.SelectedItem = overview.Items.OfType<ListBoxItem>().FirstOrDefault(i =>
                i.Tag is HistoryOverviewItem item && item.Key == key && PeriodOf(item) == selectedPeriod);
        }
        finally { loading = false; }
    }

    private int MeasuredDays(PeriodSelection selection) =>
        days.Count(day => day.Slices.Any(s => PeriodOf(s) == selection && s.Observed is not null));

    private string PeriodTitle(DailyHistorySlice slice)
    {
        var title = $"{slice.Plan ?? localizer.Text(UiText.PlanUnknown)} · {localizer.Unit(slice.Unit)} · "
            + $"{slice.Window.StartsAt?.ToOffset(JapaneseCalendar.Offset).ToString("g", localizer.Culture)} – "
            + $"{slice.Window.ResetsAt?.ToOffset(JapaneseCalendar.Offset).ToString("g", localizer.Culture)}"
            + $" · {Denominator(slice.PeriodId)}";
        var own = MeasuredDays(PeriodOf(slice));
        // Flag a period with only a couple of observed days when another period of the same selection has clearly more.
        var richest = days.SelectMany(d => d.Slices).Select(PeriodOf).Distinct().Max(MeasuredDays);
        return own <= 2 && richest > own ? title + " · " + localizer.Text(UiText.PeriodFewRecords) : title;
    }

    private string Denominator(string? periodId)
    {
        var fields = periodId?.Split('|', 6);
        var value = fields is { Length: >= 5 } && decimal.TryParse(fields[4], NumberStyles.Number,
            CultureInfo.InvariantCulture, out var total) ? localizer.Amount(total) : "—";
        return localizer.Format(UiText.HistoryPeriodDenominator, value);
    }

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
        showGraph.IsEnabled = false;
        graphStatus.Text = "";
        if (days.Count == 0) return;
        var selected = (period.SelectedItem as ComboBoxItem)?.Tag as PeriodSelection;
        var slices = days.Select(d => d.Slices.SingleOrDefault(s => PeriodOf(s) == selected)).ToArray();
        if (selectedDay is null || !days.Any(d => d.Date == selectedDay)) selectedDay = days[^1].Date;
        var maximumObserved = slices.Max(s => s?.Observed ?? 0);
        decimal maximum = (maximumObserved > 0 ? maximumObserved
            : Math.Max(1, slices.Max(s => s?.Guidance ?? 0))) * 1.2m;
        var hasCumulative = slices.Any(s => s?.Cumulative is not null);
        var width = Math.Max(100, chart.ActualWidth - (hasCumulative ? 104 : 56));
        const double height = 208;
        double cell = width / days.Count;
        var unit = slices.FirstOrDefault(s => s is not null)?.Unit ?? UsageUnit.Unknown;
        var cumulativeMaximum = slices.Max(s => s?.Cumulative ?? 0);
        if (cumulativeMaximum == 0) cumulativeMaximum = 1;
        if (slices.All(s => s?.Observed is null))
            graphStatus.Text = localizer.Text(UiText.HistoryGraphNoData);
        else if (slices.All(s => s?.Observed is null or 0))
            graphStatus.Text = localizer.Text(slices.Any(s => s?.Observed is null)
                ? UiText.HistoryGraphZeroWithGaps : UiText.HistoryGraphZero);
        if (slices.Any(s => s?.Guidance > maximum))
            graphStatus.Text += (graphStatus.Text.Length == 0 ? "" : "\n") + localizer.Text(UiText.HistoryGraphGuidanceAbove);
        for (int step = 0; step <= 2; step++)
        {
            var y = height - height * step / 2;
            var line = new Line { X1 = 44, X2 = width + 44, Y1 = y, Y2 = y,
                Stroke = Brush("BorderBrush"), StrokeThickness = 1, IsHitTestVisible = false };
            chart.Children.Add(line);
            var label = Label(localizer.AxisNumber(maximum * step / 2)
                + (step == 2 ? " " + localizer.Unit(unit) : ""), 10);
            Canvas.SetTop(label, y - 12); chart.Children.Add(label);
            if (hasCumulative)
            {
                var axisValue = step == 2 ? cumulativeMaximum : step == 1 ? cumulativeMaximum / 2 : 0;
                var right = Label(localizer.AxisNumber(axisValue), 10);
                Canvas.SetLeft(right, width + 48); Canvas.SetTop(right, y - 12); chart.Children.Add(right);
            }
        }
        for (int i = 0; i < days.Count; i++)
        {
            var slice = slices[i];
            bool partial = IsPartial(days[i].Date, slice);
            var measured = slice?.Observed;
            double barHeight = measured is { } value ? Math.Max(2, height * (double)(value / maximum)) : height;
            var inset = Math.Max(0, Math.Min(3, (cell - 2) / 2));
            var bar = new Button { Width = cell, Height = height, Tag = barHeight,
                Padding = new Thickness(inset, 0, inset, 0),
                Background = measured is null || partial ? Hatch() : Brush("AccentBrush"),
                BorderBrush = Brush(measured is null ? "BorderBrush" : "AccentBrush"),
                BorderThickness = new Thickness(days[i].Date == selectedDay ? 3 : 1),
                Opacity = measured is null ? 0.5 : 1,
                Style = (Style)FindResource("HistoryBarButton") };
            Canvas.SetLeft(bar, 44 + cell * i); Canvas.SetTop(bar, 0);
            var day = days[i].Date;
            bar.ToolTip = Detail(day, slice, partial);
            AutomationProperties.SetName(bar, AutomationText.ForButtonLike(Detail(day, slice, partial)));
            bar.Click += (_, _) =>
            {
                selectedDay = day;
                foreach (var item in chart.Children.OfType<Button>()) item.BorderThickness = new Thickness(1);
                bar.BorderThickness = new Thickness(3);
                detail.Text = Detail(day, slice, partial);
            };
            chart.Children.Add(bar);
            if (slice?.Guidance is { } guidance && guidance <= maximum)
            {
                double y = height - height * (double)(guidance / maximum);
                chart.Children.Add(new Line { X1 = 44 + cell * i, X2 = 44 + cell * (i + 1), Y1 = y, Y2 = y,
                    Stroke = Brush("WarningBrush"), StrokeThickness = 2, StrokeDashArray = [3, 2],
                    IsHitTestVisible = false });
            }
            if (days.Count <= 7 || i % (days.Count <= 31 ? 5 : 14) == 0)
            {
                var label = Label(day.ToString("M/d", CultureInfo.InvariantCulture), 10);
                Canvas.SetLeft(label, 44 + cell * i); Canvas.SetTop(label, height);
                chart.Children.Add(label);
            }
        }
        Point? previous = null;
        foreach (var (slice, index) in slices.Select((s, i) => (s, i)))
        {
            if (slice?.Cumulative is not { } value)
            {
                previous = null;
                continue;
            }
            var point = new Point(44 + cell * (index + 0.5), height - height * (double)(value / cumulativeMaximum));
            if (previous is { } last)
                chart.Children.Add(new Line { X1 = last.X, Y1 = last.Y, X2 = point.X, Y2 = point.Y,
                    Stroke = Brush("TextBrush"), StrokeThickness = 2, IsHitTestVisible = false });
            var marker = new Ellipse { Width = 6, Height = 6, Fill = Brush("TextBrush"), IsHitTestVisible = false };
            Canvas.SetLeft(marker, point.X - 3); Canvas.SetTop(marker, point.Y - 3);
            chart.Children.Add(marker);
            previous = point;
        }
        var selectedIndex = days.FindIndex(d => d.Date == selectedDay);
        detail.Text = Detail(days[selectedIndex].Date, slices[selectedIndex], IsPartial(days[selectedIndex].Date, slices[selectedIndex]));
        showGraph.IsEnabled = true;
    }

    private static bool IsPartial(DateOnly date, DailyHistorySlice? slice)
        => DailyHistory.IsPartial(date, slice, DateTimeOffset.UtcNow);

    private string Detail(DateOnly date, DailyHistorySlice? slice, bool partial) =>
        localizer.Format(UiText.DayDetail, date.ToString("d", localizer.Culture),
            localizer.Text(slice?.Observed is null ? UiText.Unmeasured : partial ? UiText.Partial : UiText.CompleteToObservation),
            slice?.Observed is { } observed ? localizer.Amount(observed) : "—", localizer.Unit(slice?.Unit ?? UsageUnit.Unknown),
            localizer.Number(slice?.Guidance, 2),
            localizer.Number(slice?.GuidancePercent, 1),
            slice?.ObservedThrough?.ToOffset(JapaneseCalendar.Offset).ToString("g", localizer.Culture) ?? localizer.Text(UiText.NoRecord))
        + "\n" + localizer.Format(UiText.HistoryCumulative,
            slice?.Cumulative is { } cumulative ? localizer.Amount(cumulative) : "—", localizer.Unit(slice?.Unit ?? UsageUnit.Unknown))
        + (slice is null ? "" : "\n" + localizer.Text(UiText.Changes) + ": " + string.Join(", ", slice.Events)
            + (slice.UnallocatedBoundary ? " · " + localizer.Text(UiText.DayBoundaryUnallocated) : "")
            + (slice.HasGaps ? " · " + localizer.Text(UiText.ObservationGap) : "")
            + (slice.ExcludedRecovery > 0 ? "\n" + localizer.Format(UiText.HistoryRecoveryExcluded,
                localizer.Amount(slice.ExcludedRecovery), localizer.Unit(slice.Unit)) : ""));

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

internal sealed class HistoryChartCanvas : Canvas
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(HistoryChartCanvas owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore()
            => System.Windows.Automation.Peers.AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(HistoryChartCanvas);
    }
}
