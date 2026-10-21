using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Ansible;

public sealed partial class DictationPage : Page
{
    private static readonly TimeSpan CopiedDuration = TimeSpan.FromSeconds(2);

    private readonly DictationSession _session;
    private readonly AppPaths _paths;
    private readonly HomeDictationHistory _history;
    private readonly Func<nint> _windowHandle;
    private readonly Task _catalogNamesTask;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _copiedTimer = new() { Interval = CopiedDuration };
    private SessionSnapshot _state;
    private UiSessionObserver? _observer;
    private bool _picking;
    private readonly Dictionary<string, string> _homeCatalogNames = new(StringComparer.OrdinalIgnoreCase);
    private UsageDocument? _homeUsage;
    private string? _homeUsageError;
    private string _homeShortcutDisplay = "";
    private int _homeGreetingHour = -1;
    private bool _homeDataInitialized;
    private DateTime _homeDataDate;
    private bool _disposed;

    internal DictationPage(DictationSession session, AppPaths paths, Func<nint> windowHandle, HomeDictationHistory history)
    {
        InitializeComponent();
        _session = session;
        _paths = paths;
        _windowHandle = windowHandle;
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _state = session.State;
        _catalogNamesTask = LoadHomeCatalogNamesAsync();
        _clock.Tick += (_, _) =>
        {
            RenderClock();
            var dateChanged = _homeDataDate != DateTime.Today;
            if (!dateChanged && _homeGreetingHour == DateTime.Now.Hour) return;
            RenderHomeData();
            if (dateChanged) RenderHomeHistory();
        };
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); CopyLabel.Text = "Copy"; };
        _history.Changed += HistoryChanged;
        HistoryView.QueryChanged += HistoryQueryChanged;
        HistoryView.CopyRequested += CopyHistoryItem;
        HistoryChanged();
        _observer = new UiSessionObserver(session, DispatcherQueue, state =>
        {
            _state = state;
            if (IsLoaded) { Render(); }
        });
        Loaded += (_, _) =>
        {
            _clock.Start();
            Render();
            RenderHomeHistory();
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
        _history.Changed -= HistoryChanged;
        HistoryView.QueryChanged -= HistoryQueryChanged;
        HistoryView.CopyRequested -= CopyHistoryItem;
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
        var retainedInHistory = _state.TranscriptOperationId is { } operationId && _history.Contains(operationId);
        var showTranscript = !empty && !retainedInHistory;
        TranscriptCard.Visibility = showTranscript ? Visibility.Visible : Visibility.Collapsed;
        TranscriptHeader.Visibility = showTranscript ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = _state.IsIdle || _state.Phase == DictationPhase.RecoveryRequired;
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
                _homeDataInitialized = false;
                RenderHomeData();
                RenderHomeHistory();
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
                    ShortcutKeys.Children.Add(new TextBlock { Text = "+", Style = AppStyle("CaptionSecondaryStyle"), VerticalAlignment = VerticalAlignment.Center });
                var keycap = new Border { Style = AppStyle("KeycapStyle") };
                keycap.Child = new TextBlock { Text = parts[index], Style = AppStyle("KeycapTextStyle") };
                ShortcutKeys.Children.Add(keycap);
            }
        }
        if (ShortcutTailText.Text != "anywhere, or") ShortcutTailText.Text = "anywhere, or";
        if (!_homeDataInitialized || _homeDataDate != DateTime.Today || !ReferenceEquals(_homeUsage, _state.Usage) || _homeUsageError != _state.UsageError)
        {
            _homeDataInitialized = true;
            _homeDataDate = DateTime.Today;
            _homeUsage = _state.Usage;
            _homeUsageError = _state.UsageError;
            HomeInsightsColumn.Render(HomeInsightsProjection.Create(_state.Usage, _state.UsageError,
                HomeModelDisplayName, DateOnly.FromDateTime(DateTime.Today), TimeZoneInfo.Local));
        }
    }

    private void RenderHomeHistory()
    {
        HistoryView.Render(HomeHistoryProjection.Create(_history.Snapshot(), HistoryView.Query,
            _history.OversizedCurrentResult, HomeModelDisplayName, DateTime.Today));
    }

    private void HistoryChanged()
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !IsLoaded) return;
            Render();
            RenderHomeHistory();
        });
    }

    private void HistoryQueryChanged() => RenderHomeHistory();

    private void CopyHistoryItem(HomeHistoryItem item)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(item.Text);
            Clipboard.SetContent(package);
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    private Style AppStyle(string key)
    {
        var resource = Resources.ContainsKey(key) ? Resources[key] : Application.Current.Resources[key];
        return (Style)resource;
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
    private void HomeColumnsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < (double)Application.Current.Resources["HomeColumnsStackThreshold"];
        HomeColumns.ColumnDefinitions.Clear();
        HomeColumns.RowDefinitions.Clear();
        if (narrow)
        {
            HomeColumns.ClearValue(FrameworkElement.HeightProperty);
            HomeColumns.ClearValue(FrameworkElement.MinHeightProperty);
            HomeInsightsColumn.SetWideLayout(false);
            HomeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HomeColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HomeColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(HomeInsightsColumn, 0);
            Grid.SetRow(HomeInsightsColumn, 1);
        }
        else
        {
            HomeInsightsColumn.SetWideLayout(true);
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
        if (!notice.ShouldDisplayOn(NoticeArea.Dictation))
        {
            StatusInfo.IsOpen = false;
            StatusInfo.Visibility = Visibility.Collapsed;
            return;
        }
        StatusInfo.Severity = notice.Kind == NoticeKind.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
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
