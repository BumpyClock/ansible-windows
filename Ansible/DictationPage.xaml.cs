using Ansible.Core;
using Microsoft.UI.Text;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace Ansible;

public sealed partial class DictationPage : Page
{
    private static readonly TimeSpan CopiedDuration = TimeSpan.FromSeconds(2);

    private readonly DictationSession _session;
    private readonly AppPaths _paths;
    private readonly Func<nint> _windowHandle;
    private readonly Task _catalogNamesTask;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _copiedTimer = new() { Interval = CopiedDuration };
    private SessionSnapshot _state;
    private UiSessionObserver? _observer;
    private bool _picking;
    private double _homeCellSize = 14;
    private UsageSummary? _homeSummary;
    private UsageDocument? _homeUsage;
    private string? _homeUsageError;
    private readonly Dictionary<string, string> _homeCatalogNames = new(StringComparer.OrdinalIgnoreCase);
    private long _homeHistoryVersion = -1;
    private string _homeHistoryQuery = "";
    private volatile bool _historyLimitMessageVisible;
    private bool _homeHistoryLimitMessageRendered;
    private string _homeShortcutDisplay = "";
    private int _homeGreetingHour = -1;
    private bool _homeDataInitialized;
    private bool _disposed;

    internal DictationPage(DictationSession session, AppPaths paths, Func<nint> windowHandle)
    {
        InitializeComponent();
        _session = session;
        _paths = paths;
        _windowHandle = windowHandle;
        _state = session.State;
        _catalogNamesTask = LoadHomeCatalogNamesAsync();
        _clock.Tick += (_, _) => RenderClock();
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); CopyLabel.Text = "Copy"; };
        _session.StateChanged += CaptureHistory;
        CaptureHistory(_state);
        _observer = new UiSessionObserver(session, DispatcherQueue, state =>
        {
            _state = state;
            if (IsLoaded) { Render(); }
        });
        Loaded += (_, _) =>
        {
            _clock.Start();
            Render();
        };
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            if (_copiedTimer.IsEnabled) { _copiedTimer.Stop(); CopyLabel.Text = "Copy"; }
        };
    }

    internal Task FinishBackgroundWorkAsync() => _catalogNamesTask;

    internal void Dispose()
    {
        _disposed = true;
        _session.StateChanged -= CaptureHistory;
        _observer?.Dispose();
        _observer = null;
        _clock.Stop();
        if (_copiedTimer.IsEnabled) { _copiedTimer.Stop(); CopyLabel.Text = "Copy"; }
    }

    private void Render()
    {
        RenderRecordControl();
        RenderStatus();
        RenderClock();
        RenderNotice();
        RenderHomeData();
        if (TranscriptBox.Text != _state.Transcript) { TranscriptBox.Text = _state.Transcript; }
        var empty = string.IsNullOrWhiteSpace(_state.Transcript);
        var completedDictation = _state.Activity == SessionActivity.Dictation &&
            _state.Phase == DictationPhase.Ready && _state.Outcome == SessionOutcomeKind.Completed &&
            _state.Result is not null;
        var historyText = _state.Result is { } result
            ? (string.IsNullOrWhiteSpace(result.SpeechText) ? result.DisplayText : result.SpeechText).Trim() : "";
        var showTranscript = !empty && (!completedDictation || historyText.Length > HomeDictationHistory.MaximumCharacters);
        TranscriptCard.Visibility = showTranscript ? Visibility.Visible : Visibility.Collapsed;
        TranscriptHeader.Visibility = showTranscript ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = _state.IsIdle || _state.Phase == DictationPhase.RecoveryRequired;
    }

    private void CaptureHistory(SessionSnapshot state)
    {
        if (state.Phase != DictationPhase.Ready || state.Activity != SessionActivity.Dictation ||
            state.OperationId is not Guid operationId) { return; }
        if (state.Outcome != SessionOutcomeKind.Completed || state.Result is not { } result ||
            state.CompletedAt is not { } completedAt)
        {
            return;
        }
        var text = string.IsNullOrWhiteSpace(result.SpeechText) ? result.DisplayText : result.SpeechText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        var item = new HomeHistoryItem(operationId, completedAt, text.Trim(), state.SelectedModel?.Id ?? "",
            result.SpokenWords, state.Elapsed);
        if (item.Text.Length > HomeDictationHistory.MaximumCharacters)
        {
            _historyLimitMessageVisible = true;
            return;
        }
        HomeDictationHistory.Add(item);
    }

    private async Task LoadHomeCatalogNamesAsync()
    {
        var dispatcher = DispatcherQueue;
        try
        {
            var entries = await NativeModelCatalog.LoadAsync(_paths.ModelCatalog).ConfigureAwait(false);
            var names = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.DisplayName))
                .ToDictionary(entry => entry.Id, entry => entry.DisplayName!, StringComparer.OrdinalIgnoreCase);
            dispatcher.TryEnqueue(() =>
            {
                if (_disposed) { return; }
                _homeCatalogNames.Clear();
                foreach (var pair in names) { _homeCatalogNames[pair.Key] = pair.Value; }
                if (_homeSummary is not null)
                {
                    if (_homeSummary is { } summary) { RenderHomeModels(summary); }
                    RenderHistory();
                }
            });
        }
        catch
        {
            // Installed model snapshots still carry catalog display names. An unavailable catalog adds no label.
        }
    }

    private string? HomeModelDisplayName(string modelId)
    {
        if (_homeCatalogNames.TryGetValue(modelId, out var catalogName)) { return catalogName; }
        return _state.Models.FirstOrDefault(model => model.Id == modelId)?.DisplayName;
    }

    private void RenderHomeData()
    {
        var hour = DateTime.Now.Hour;
        if (hour != _homeGreetingHour)
        {
            _homeGreetingHour = hour;
            GreetingText.Text = hour switch
            {
                < 12 => "Good morning",
                < 18 => "Good afternoon",
                _ => "Good evening"
            };
        }
        var settings = _state.Settings;
        var shortcutMode = settings.PushToTalk ? "Hold" : "Press";
        if (ShortcutModeText.Text != shortcutMode) ShortcutModeText.Text = shortcutMode;
        if (_homeShortcutDisplay != settings.Shortcut.DisplayText)
        {
            _homeShortcutDisplay = settings.Shortcut.DisplayText;
            ShortcutKeys.Children.Clear();
            var parts = _homeShortcutDisplay.Split(" + ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parts.Length; index++)
            {
                if (index > 0)
                {
                    ShortcutKeys.Children.Add(new TextBlock
                    {
                        Text = "+",
                        Style = AppStyle("CaptionSecondaryStyle"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                var keycap = new Border { Style = AppStyle("KeycapStyle") };
                keycap.Child = new TextBlock { Text = parts[index], Style = AppStyle("KeycapTextStyle") };
                ShortcutKeys.Children.Add(keycap);
            }
        }
        const string shortcutTail = "anywhere, or";
        if (ShortcutTailText.Text != shortcutTail) ShortcutTailText.Text = shortcutTail;
        RenderRecordControl();
        var query = HistorySearchBox?.Text?.Trim() ?? "";
        if (_homeHistoryVersion != HomeDictationHistory.Version || _homeHistoryQuery != query ||
            _homeHistoryLimitMessageRendered != _historyLimitMessageVisible)
        {
            _homeHistoryVersion = HomeDictationHistory.Version;
            _homeHistoryQuery = query;
            RenderHistory();
        }
        if (_homeDataInitialized && ReferenceEquals(_homeUsage, _state.Usage) && _homeUsageError == _state.UsageError) { return; }
        _homeDataInitialized = true;
        _homeUsage = _state.Usage;
        _homeUsageError = _state.UsageError;
        if (_state.Usage is null)
        {
            HomePaceText.Text = "--";
            HomeSessionsText.Text = "--";
            HomeWordsText.Text = "--";
            HomeStreakText.Text = "--";
            HomeInsightsFootnote.Text = _state.UsageError ?? "Usage statistics are not available yet.";
            HomeModelRows.Children.Clear();
            HomeModelRows.Children.Add(new TextBlock
            {
                Text = "Usage statistics are not available yet.",
                TextWrapping = TextWrapping.Wrap,
                Style = AppStyle("SecondaryTextStyle")
            });
            _homeSummary = null;
            RenderHomeActivity(null);
            return;
        }
        _homeSummary = UsageSummary.Create(_state.Usage.Entries, DateOnly.FromDateTime(DateTime.Today), TimeZoneInfo.Local);
        HomePaceText.Text = _homeSummary.WordsPerMinute?.ToString("N0", CultureInfo.CurrentCulture) ?? "--";
        HomeSessionsText.Text = _homeSummary.DictationSessions.ToString("N0", CultureInfo.CurrentCulture);
        HomeWordsText.Text = _homeSummary.DictatedWords.ToString("N0", CultureInfo.CurrentCulture);
        HomeStreakText.Text = _homeSummary.CurrentStreak == 0 ? "No streak" :
            $"{_homeSummary.CurrentStreak} {(_homeSummary.CurrentStreak == 1 ? "day" : "days")} streak";
        HomeInsightsFootnote.Text = _homeSummary.RecordingSeconds > 0
            ? $"{_homeSummary.RecordingSeconds / 60:N1} min of microphone time, pauses included."
            : _homeSummary.DictationSessions == 0 ? "No completed dictations yet." : "Microphone time is unavailable.";
        RenderHomeModels(_homeSummary);
        RenderHomeActivity(_homeSummary);
    }

    private Style AppStyle(string key)
    {
        var resource = Resources.ContainsKey(key) ? Resources[key] : Application.Current.Resources[key];
        return (Style)resource;
    }

    private void RenderHistory()
    {
        if (HistoryRows is null) { return; }
        _homeHistoryLimitMessageRendered = _historyLimitMessageVisible;
        var all = HomeDictationHistory.Snapshot();
        var query = HistorySearchBox?.Text?.Trim() ?? "";
        var items = query.Length == 0
            ? all
            : all.Where(item => item.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var todayCount = all.Count(item => item.CompletedAt.LocalDateTime.Date == DateTime.Today);
        HistoryCount.Text = $"{todayCount:N0} {(todayCount == 1 ? "dictation" : "dictations")} today";
        HistoryRows.Children.Clear();
        HistoryRows.VerticalAlignment = items.Count == 0 ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        if (_historyLimitMessageVisible)
        {
            HistoryRows.Children.Add(new TextBlock
            {
                Text = $"A transcript over {HomeDictationHistory.MaximumCharacters:N0} characters stays in the current result and is not added to session history.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 8, 12, 4),
                Style = AppStyle("CaptionSecondaryStyle")
            });
        }
        if (items.Count == 0)
        {
            var emptyState = new StackPanel
            {
                Spacing = (double)Application.Current.Resources["HistoryEmptyGap"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            emptyState.Children.Add(new TextBlock
            {
                Text = all.Count == 0 ? "No completed dictations in this app session." : "No matching transcripts.",
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = AppStyle("HistoryEmptyTitleStyle")
            });
            emptyState.Children.Add(new TextBlock
            {
                Text = all.Count == 0 ? "Choose Dictate to start your history." : "Try a different search.",
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = AppStyle("HistoryEmptyDetailStyle")
            });
            HistoryRows.Children.Add(emptyState);
            return;
        }
        DateOnly? group = null;
        foreach (var item in items)
        {
            var date = DateOnly.FromDateTime(item.CompletedAt.LocalDateTime);
            if (group != date)
            {
                group = date;
                var label = date == DateOnly.FromDateTime(DateTime.Today) ? "TODAY" :
                    date == DateOnly.FromDateTime(DateTime.Today.AddDays(-1)) ? "YESTERDAY" :
                    date.ToString("ddd, MMM d", CultureInfo.CurrentCulture).ToUpperInvariant();
                var divider = new Grid { Margin = new Thickness(8, 12, 8, 4), ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
                divider.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                divider.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                divider.Children.Add(new TextBlock { Text = label, Style = AppStyle("CaptionSecondaryStyle"), FontWeight = FontWeights.SemiBold });
                var line = new Border { Style = AppStyle("HistoryDividerStyle") };
                Grid.SetColumn(line, 1);
                divider.Children.Add(line);
                HistoryRows.Children.Add(divider);
            }
            var row = new Border { Style = AppStyle("HistoryRowStyle") };
            var content = new Grid { ColumnSpacing = 16 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(new TextBlock
            {
                Text = item.CompletedAt.ToLocalTime().ToString("h:mmtt", CultureInfo.CurrentCulture),
                Style = AppStyle("CaptionSecondaryStyle"), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 0, 0)
            });
            var text = new TextBlock
            {
                Text = item.Text, TextWrapping = TextWrapping.Wrap,
                Style = AppStyle("BodyTextBlockStyle"), LineHeight = 22
            };
            var modelName = item.ModelId.Length > 0 ? HomeModelDisplayName(item.ModelId) : null;
            var meta = new TextBlock
            {
                Text = $"{(item.Words is { } words ? $"{words:N0} {(words == 1 ? "word" : "words")}" : "Word count unavailable")} · {item.Elapsed:mm\\:ss}" +
                    (modelName is { Length: > 0 } ? $" · {modelName}" : ""),
                Style = AppStyle("CaptionSecondaryStyle"), Margin = new Thickness(0, 4, 0, 0)
            };
            var body = new StackPanel { Spacing = 2 };
            body.Children.Add(text);
            body.Children.Add(meta);
            Grid.SetColumn(body, 1);
            content.Children.Add(body);
            var copy = new Button { Width = 32, Height = 32, Padding = new Thickness(0), Tag = item, VerticalAlignment = VerticalAlignment.Top };
            ToolTipService.SetToolTip(copy, "Copy transcript");
            copy.Content = new FontIcon { Glyph = "\uE8C8", FontSize = 16 };
            copy.Click += HistoryCopyClicked;
            Grid.SetColumn(copy, 2);
            content.Children.Add(copy);
            row.Child = content;
            HistoryRows.Children.Add(row);
        }
    }

    private void HistorySearchChanged(object sender, TextChangedEventArgs args)
    {
        _homeHistoryQuery = HistorySearchBox.Text.Trim();
        _homeHistoryVersion = HomeDictationHistory.Version;
        RenderHistory();
    }

    private void HistoryCopyClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: HomeHistoryItem item }) { return; }
        try
        {
            var package = new DataPackage();
            package.SetText(item.Text);
            Clipboard.SetContent(package);
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    private void RenderHomeModels(UsageSummary summary)
    {
        HomeModelRows.Children.Clear();
        var max = Math.Max(1, summary.Models.Sum(model => model.Sessions));
        var modelIndex = 0;
        foreach (var model in summary.Models.Take(5))
        {
            var primary = modelIndex++ == 0;
            var label = HomeModelDisplayName(model.ModelId) ?? "Unknown model";
            var row = new StackPanel { Spacing = (double)Application.Current.Resources["HomeModelRowsGap"] };
            var top = new Grid { ColumnSpacing = 8 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (primary) top.Children.Add(new Ellipse { Style = AppStyle("HomeModelDotStyle") });
            var name = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = AppStyle(primary ? "BodyTextBlockStyle" : "HomeMetricLabelStyle") };
            Grid.SetColumn(name, primary ? 1 : 0);
            Grid.SetColumnSpan(name, primary ? 1 : 2);
            top.Children.Add(name);
            var count = new TextBlock { Text = model.Sessions.ToString("N0", CultureInfo.CurrentCulture), Style = AppStyle("BodyStrongTextBlockStyle") };
            Grid.SetColumn(count, 2);
            top.Children.Add(count);
            row.Children.Add(top);
            var bar = new ProgressBar
            {
                Maximum = max,
                Value = model.Sessions,
                Style = AppStyle(primary ? "HomeModelPrimaryBarStyle" : "HomeModelSecondaryBarStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            row.Children.Add(bar);
            HomeModelRows.Children.Add(row);
        }
        if (summary.Models.Count == 0)
            HomeModelRows.Children.Add(new TextBlock { Text = "No completed sessions yet.", TextWrapping = TextWrapping.Wrap, Style = AppStyle("SecondaryTextStyle") });
    }

    private void RenderHomeActivity(UsageSummary? summary)
    {
        const int weeks = 13;
        const double labelColumn = 16;
        var cellSize = _homeCellSize;
        HomeHeatmapGrid.Children.Clear();
        HomeHeatmapGrid.ColumnDefinitions.Clear();
        HomeHeatmapGrid.RowDefinitions.Clear();
        HomeHeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelColumn) });
        for (var column = 0; column < weeks; column++) HomeHeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        HomeHeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var row = 0; row < 7; row++) HomeHeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellSize) });
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-84);
        start = start.AddDays(-(int)start.DayOfWeek);
        foreach (var (row, text) in new[] { (1, "M"), (3, "W"), (5, "F") })
        {
            var label = new TextBlock { Text = text, Style = AppStyle("CaptionSecondaryStyle"), VerticalAlignment = VerticalAlignment.Center, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = cellSize };
            Grid.SetRow(label, row + 1);
            HomeHeatmapGrid.Children.Add(label);
        }
        var monthColumns = Enumerable.Range(0, weeks).Where(column => column == 0 || start.AddDays(column * 7).Month != start.AddDays(column * 7 - 7).Month).ToList();
        if (monthColumns.Count > 1 && monthColumns[1] < 3) monthColumns.RemoveAt(0);
        for (var index = 0; index < monthColumns.Count; index++)
        {
            var column = monthColumns[index];
            var month = new TextBlock { Text = start.AddDays(column * 7).ToString("MMM", CultureInfo.CurrentCulture), Style = AppStyle("CaptionTextBlockStyle"), TextWrapping = TextWrapping.NoWrap };
            Grid.SetColumn(month, column + 1);
            Grid.SetColumnSpan(month, (index + 1 < monthColumns.Count ? monthColumns[index + 1] : weeks) - column);
            HomeHeatmapGrid.Children.Add(month);
        }
        for (var column = 0; column < weeks; column++)
            for (var row = 0; row < 7; row++)
            {
                var day = start.AddDays(column * 7 + row);
                var count = summary?.Activity.GetValueOrDefault(day) ?? 0;
                var styleKey = day > today ? "ActivityFutureCellStyle" : count switch { 0 => "ActivityEmptyCellStyle", 1 => "ActivityLowCellStyle", 2 => "ActivityMediumCellStyle", _ => "ActivityHighCellStyle" };
                var cell = new Border { Style = AppStyle(styleKey), Width = cellSize, Height = cellSize, CornerRadius = new CornerRadius(3) };
                var description = $"{day.ToString("ddd d MMM", CultureInfo.CurrentCulture)}: {count} {(count == 1 ? "session" : "sessions")}";
                ToolTipService.SetToolTip(cell, description);
                AutomationProperties.SetName(cell, description);
                Grid.SetRow(cell, row + 1);
                Grid.SetColumn(cell, column + 1);
                HomeHeatmapGrid.Children.Add(cell);
            }
    }

    private void HomeHeatmapHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var gap = (double)Application.Current.Resources["HomeActivityHeatmapGap"];
        var cell = Math.Clamp(Math.Floor((args.NewSize.Width - 16 - 13 * gap) / 13), 12, 20);
        if (cell == _homeCellSize) return;
        _homeCellSize = cell;
        RenderHomeActivity(_homeSummary);
    }

    private void HomeHeaderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < (double)Application.Current.Resources["HomeHeaderStackThreshold"];
        HomeHeader.ColumnDefinitions.Clear();
        HomeHeader.RowDefinitions.Clear();
        HomeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        HomeHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (narrow) HomeHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        else HomeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(CompactDictateCard, narrow ? 0 : 1);
        Grid.SetRow(CompactDictateCard, narrow ? 1 : 0);
        CompactDictateCard.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        var stackedHelper = args.NewSize.Width < (double)Application.Current.Resources["ShortcutStackThreshold"];
        Grid.SetRow(ShortcutButtons, stackedHelper ? 1 : 0);
        Grid.SetColumn(ShortcutButtons, stackedHelper ? 0 : 1);
        Grid.SetColumnSpan(ShortcutButtons, stackedHelper ? 2 : 1);
        ShortcutButtons.HorizontalAlignment = stackedHelper ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        RequestHomeColumnsHeight();
    }
    private void HistoryHeaderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < (double)Application.Current.Resources["HistorySearchStackThreshold"];
        HistoryHeaderGrid.ColumnDefinitions.Clear();
        HistoryHeaderGrid.RowDefinitions.Clear();
        if (narrow)
        {
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HistoryHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HistoryHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(HistoryTitle, 0);
            Grid.SetColumn(HistoryCountPill, 1);
            Grid.SetColumn(HistorySearchBox, 0);
            Grid.SetColumnSpan(HistorySearchBox, 2);
            Grid.SetRow(HistorySearchBox, 1);
            Grid.SetColumn(HistorySearchIcon, 0);
            Grid.SetColumnSpan(HistorySearchIcon, 2);
            Grid.SetRow(HistorySearchIcon, 1);
            Grid.SetColumn(HistorySearchShortcut, 0);
            Grid.SetColumnSpan(HistorySearchShortcut, 2);
            Grid.SetRow(HistorySearchShortcut, 1);
            HistorySearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            HistoryHeaderGrid.RowSpacing = 10;
            HistorySearchBox.Margin = new Thickness(0);
        }
        else
        {
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)Application.Current.Resources["HistorySearchColumnWidth"] });
            HistoryHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(HistoryTitle, 0);
            Grid.SetColumn(HistoryCountPill, 1);
            Grid.SetColumn(HistorySearchBox, 3);
            Grid.SetColumnSpan(HistorySearchBox, 1);
            Grid.SetRow(HistorySearchBox, 0);
            Grid.SetColumn(HistorySearchIcon, 3);
            Grid.SetColumnSpan(HistorySearchIcon, 1);
            Grid.SetRow(HistorySearchIcon, 0);
            Grid.SetColumn(HistorySearchShortcut, 3);
            Grid.SetColumnSpan(HistorySearchShortcut, 1);
            Grid.SetRow(HistorySearchShortcut, 0);
            HistorySearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            HistoryHeaderGrid.RowSpacing = 0;
            HistorySearchBox.Margin = new Thickness(0);
        }
    }

    private void FocusHistorySearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        HistorySearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void HomeColumnsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < (double)Application.Current.Resources["HomeColumnsStackThreshold"];
        HomeColumns.ColumnDefinitions.Clear();
        HomeColumns.RowDefinitions.Clear();
        if (narrow)
        {
            HomeColumns.ClearValue(FrameworkElement.HeightProperty);
            HomeColumns.ClearValue(FrameworkElement.MinHeightProperty);
            HomeActivityCard.ClearValue(FrameworkElement.MinHeightProperty);
            HomeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HomeColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HomeColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(HomeInsightsColumn, 0);
            Grid.SetRow(HomeInsightsColumn, 1);
        }
        else
        {
            HomeActivityCard.MinHeight = (double)Application.Current.Resources["HomeActivityCardMinHeight"];
            HomeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HomeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)Application.Current.Resources["HomeInsightsColumnWidth"] });
            HomeColumns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(HomeInsightsColumn, 1);
            Grid.SetRow(HomeInsightsColumn, 0);
            RequestHomeColumnsHeight();
        }
    }

    private void HomeViewportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateHomeColumnsHeight(HomeViewport.ViewportHeight);

    private void StatusInfoSizeChanged(object sender, SizeChangedEventArgs args) => RequestHomeColumnsHeight();

    private void RequestHomeColumnsHeight()
    {
        if (!IsLoaded) { return; }
        DispatcherQueue.TryEnqueue(() => UpdateHomeColumnsHeight(HomeViewport.ViewportHeight));
    }

    private void UpdateHomeColumnsHeight(double viewportHeight)
    {
        if (viewportHeight <= 0 || HomeColumns.ActualWidth < (double)Application.Current.Resources["HomeColumnsStackThreshold"]) { return; }
        var padding = HomeViewport.ContentPadding;
        var effectiveViewportHeight = HomeViewport.ViewportHeight > 0 ? HomeViewport.ViewportHeight : viewportHeight;
        var spacing = (double)Application.Current.Resources["PageSectionSpacing"];
        var statusVisible = StatusInfo.Visibility == Visibility.Visible;
        var precedingSpacing = spacing * (statusVisible ? 2 : 1);
        var available = effectiveViewportHeight - padding.Top - padding.Bottom - HomeHeader.ActualHeight - precedingSpacing -
            (statusVisible ? StatusInfo.ActualHeight : 0);
        if (available > 0 && (double.IsNaN(HomeColumns.MinHeight) || Math.Abs(HomeColumns.MinHeight - available) > 0.5))
        {
            HomeColumns.ClearValue(FrameworkElement.HeightProperty);
            if (double.IsNaN(HomeColumns.MinHeight) || Math.Abs(HomeColumns.MinHeight - available) > 0.5)
                HomeColumns.MinHeight = available;
        }
    }

    private void RenderRecordControl()
    {
        var canRecord = !_picking && (_state.CanStart || _state.CanFinish);
        CompactDictateButton.IsEnabled = canRecord;
        var label = _state.CanFinish ? "Finish dictation" : "Start dictation";
        CompactDictateLabel.Text = _state.CanFinish ? "Finish" : "Dictate";
        AutomationProperties.SetName(CompactDictateButton, label);
        ToolTipService.SetToolTip(CompactDictateButton, label);
    }

    private void RenderStatus()
    {
        var preview = _state.SelectedModel?.Preview;
        var settings = _state.Settings;
        var (title, detail) = _state.Phase switch
        {
            DictationPhase.Disconnected => ("No speech model loaded",
                _state.Models.Count == 0 ? "Download a model in Speech models to start." : _state.Notice.Message),
            DictationPhase.Connecting => ("Loading models", "Opening the native backend."),
            DictationPhase.Ready when _state.CanStart => ("Ready to dictate", ""),
            DictationPhase.Ready when _state.SelectedModel is null => ("Choose a speech model", "Pick an installed model to begin."),
            DictationPhase.Ready => ("File transcription only", "Choose a WAV file, or pick a model that supports microphone dictation."),
            DictationPhase.Preparing => ("Preparing", "Loading the model before audio starts."),
            DictationPhase.Recording when _state.IsReplay => ("Replaying WAV", "No microphone is open."),
            DictationPhase.Recording => ("Listening", preview switch
            {
                "live" => "",
                "buffered" => "Text arrives in chunks.",
                _ => "Text appears after you finish."
            }),
            DictationPhase.Finishing => ("Finishing", "Recognizing the last audio."),
            DictationPhase.Transcribing => ("Transcribing file", _state.Notice.Message),
            DictationPhase.Cancelling => ("Cancelling", "Releasing the microphone and model."),
            DictationPhase.MaintainingModels => ("Updating models", _state.Notice.Message),
            DictationPhase.RecoveryRequired => ("Recovery required", _state.Notice.Message),
            _ => ("Closing", "Releasing native resources.")
        };
        CompactStatusTitle.Text = title;
        CompactStatusDetail.Text = detail.Length > 0 ? detail : _state.Phase == DictationPhase.Ready && _state.CanStart
            ? settings.PushToTalk
                ? $"Click the microphone, or hold {settings.Shortcut.DisplayText} in any app and release to finish."
                : $"Click the microphone, or press {settings.Shortcut.DisplayText} in any app, then press it again to finish."
            : "";
        var showCompactStatus = _state.Phase != DictationPhase.Ready || detail.Length > 0;
        CompactStatusPanel.Visibility = showCompactStatus ? Visibility.Visible : Visibility.Collapsed;
        CompactCancelButton.Visibility = _state.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        CompactCancelButton.IsEnabled = _state.CanCancel;
    }

    private void RenderClock()
    {
        var live = _state.CanCancel;
        var completed = _state.IsIdle && _state.Result is not null;
        CompactLiveText.Visibility = live || completed ? Visibility.Visible : Visibility.Collapsed;
        if (!live && !completed) { return; }
        var text = _session.Elapsed.ToString(@"mm\:ss");
        if (_state.Words is { } words) { text += $"  ·  {words:N0} {(words == 1 ? "word" : "words")}"; }
        CompactLiveText.Text = text;
    }

    private void RenderNotice()
    {
        var notice = _state.Notice;
        if (!notice.ShouldDisplay)
        {
            StatusInfo.IsOpen = false;
            StatusInfo.Visibility = Visibility.Collapsed;
            return;
        }
        StatusInfo.Severity = notice.Kind switch
        {
            NoticeKind.Error => InfoBarSeverity.Error,
            NoticeKind.Warning => InfoBarSeverity.Warning,
            NoticeKind.Success => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational
        };
        StatusInfo.Title = notice.Title;
        StatusInfo.Message = notice.Message;
        StatusInfo.Visibility = Visibility.Visible;
        StatusInfo.IsOpen = true;
    }

    private void StatusInfoClosing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton) { _session.DismissNotice(_state.Notice); }
    }

    private void StatusInfoClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (!sender.IsOpen) { sender.Visibility = Visibility.Collapsed; }
    }

    private async void RecordClicked(object sender, RoutedEventArgs args)
    {
        if (!IsEnabled) { return; }
        try
        {
            if (_state.CanFinish) { await _session.FinishAsync(); }
            else { await _session.StartDictationAsync(); }
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }
    private async void CancelClicked(object sender, RoutedEventArgs args)
    {
        try { await _session.CancelAsync(); }
        catch (Exception error) { _session.ReportUiError(error); }
    }
    internal Task TranscribeFileAsync() => PickAndRunAsync();

    private async Task PickAndRunAsync()
    {
        if (_picking) { return; }
        _picking = true;
        Render();
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wav");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
            var file = await picker.PickSingleFileAsync();
            if (file is not null && IsEnabled) { await _session.TranscribeFileAsync(file.Path); }
        }
        catch (Exception error) { _session.ReportUiError(error); }
        finally { _picking = false; Render(); }
    }

    private void CopyClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(_state.Transcript);
            Clipboard.SetContent(package);
            CopyLabel.Text = "Copied";
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }
}
