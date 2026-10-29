using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Ansible;

public sealed partial class DictationPage : Page
{
    internal event Action? ShortcutLessonRequested;
    private void ShortcutLessonClicked(object sender, RoutedEventArgs args) => ShortcutLessonRequested?.Invoke();

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
    // Removing the current transcript's history row must not bring it back as the separate transcript card.
    private Guid? _removedTranscriptOperationId;

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
            if (dateChanged) RenderHomeHistory(animate: false);
        };
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); CopyLabel.Text = "Copy"; };
        _history.Changed += HistoryChanged;
        HistoryView.QueryChanged += HistoryQueryChanged;
        HistoryView.CopyRequested += CopyHistoryItem;
        HistoryView.RemoveRequested += RemoveHistoryItem;
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
            RenderHomeHistory(animate: false);
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
        HistoryView.RemoveRequested -= RemoveHistoryItem;
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
        var retainedInHistory = _state.TranscriptOperationId is { } operationId &&
            (_history.Contains(operationId) || operationId == _removedTranscriptOperationId);
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
                RenderHomeHistory(animate: false);
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
            HomeViewport.Title = hour switch
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

    private void RenderHomeHistory(bool animate)
    {
        HistoryView.Render(HomeHistoryProjection.Create(_history.Snapshot(), HistoryView.Query,
            _history.OversizedCurrentResult, HomeModelDisplayName, DateTime.Today, _history.StorageError), animate);
    }

    private void HistoryChanged()
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !IsLoaded) return;
            Render();
            RenderHomeHistory(animate: true);
        });
    }

    private void HistoryQueryChanged() => RenderHomeHistory(animate: false);

    private void CopyHistoryItem(HomeHistoryItem item)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(item.Text);
            Clipboard.SetContent(package);
        }
        catch (Exception error)
        {
            _session.ReportUiError(error);
            return;
        }
        HistoryView.ShowCopied(item.OperationId);
    }

    // Renders synchronously so the view can move keyboard focus to the row that took the removed row's place.
    private void RemoveHistoryItem(HomeHistoryItem item)
    {
        if (!_history.Remove(item.OperationId)) { return; }
        if (item.OperationId == _state.TranscriptOperationId) { _removedTranscriptOperationId = item.OperationId; }
        RenderHomeHistory(animate: true);
    }

    private Style AppStyle(string key)
    {
        var resource = Resources.ContainsKey(key) ? Resources[key] : Application.Current.Resources[key];
        return (Style)resource;
    }

    private void DictateControlSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateShortcutLayout();
    }

    private void UpdateShortcutLayout()
    {
        var stackedHelper = HomeViewport.ContentWidth < (double)Application.Current.Resources["ShortcutStackThreshold"];
        Grid.SetRow(ShortcutButtons, stackedHelper ? 1 : 0);
        Grid.SetColumn(ShortcutButtons, stackedHelper ? 0 : 1);
        Grid.SetColumnSpan(ShortcutButtons, stackedHelper ? 2 : 1);
        ShortcutButtons.HorizontalAlignment = stackedHelper ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
    private void HomeColumnsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < (double)Application.Current.Resources["HomeColumnsStackThreshold"];
        HomeColumns.ColumnDefinitions.Clear();
        HomeColumns.RowDefinitions.Clear();
        if (narrow)
        {
            HomeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HomeColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HomeColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(HomeInsightsColumn, 0);
            Grid.SetRow(HomeInsightsColumn, 1);
        }
        else
        {
            HomeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HomeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)Application.Current.Resources["HomeInsightsColumnWidth"] });
            HomeColumns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(HomeInsightsColumn, 1);
            Grid.SetRow(HomeInsightsColumn, 0);
        }
    }

    private void HomeViewportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateShortcutLayout();

    private void RenderRecordControl()
    {
        ShortcutLessonButton.IsEnabled = _state.IsIdle;
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
