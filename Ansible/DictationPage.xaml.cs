using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Ansible;

public sealed partial class DictationPage : Page
{
    private const string HistoryNoticeKey = "history-storage";
    private const string UsageNoticeKey = "usage-statistics";
    private const string ErrorNoticeKey = "dictation-error";

    private static readonly TimeSpan CopiedDuration = TimeSpan.FromSeconds(2);

    private readonly DictationSession _session;
    private readonly AppPaths _paths;
    private readonly HomeDictationHistory _history;
    private readonly ShortcutDictation _shortcut;
    private readonly Func<nint> _windowHandle;
    private readonly Task _catalogNamesTask;
    private Task _copyPicturesTask = Task.CompletedTask;
    private bool _copyingPictures;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _copiedTimer = new() { Interval = CopiedDuration };
    private SessionSnapshot _state;
    private UiSessionObserver? _observer;
    private bool _picking;
    private readonly Dictionary<string, HomeModelName> _homeCatalogNames = new(StringComparer.OrdinalIgnoreCase);
    private UsageDocument? _homeUsage;
    private string? _homeUsageError;
    private int? _homeDictations;
    private string _homeShortcutDisplay = "";
    private int _homeGreetingHour = -1;
    private bool _homeDataInitialized;
    private DateTime _homeDataDate;
    private bool _disposed;
    // Removing the current transcript's history row must not bring it back as the separate transcript card.
    private Guid? _removedTranscriptOperationId;
    // While the page scrolls, the wide insights rail stays on screen. HistoryView pins its own header while it has rows.
    private StickyPin? _railPin;
    // Whether History holds any saved entry. Only then is Home wide with the two-column layout (UpdateHomeWidth).
    private bool _homeHasHistory;

    private readonly AppNotices _notices;

    internal DictationPage(DictationSession session, AppPaths paths, Func<nint> windowHandle, HomeDictationHistory history,
        AppNotices notices, ShortcutDictation shortcut)
    {
        InitializeComponent();
        HomeViewport.PrimaryAction = new PageAction(CompactDictateButton, RecordClicked, CompactDictateLabel);
        HomeViewport.PrimaryActionIcon = new FontIconSource { Glyph = "", FontSize = 16 };
        _session = session;
        _shortcut = shortcut;
        _shortcut.RecordResultChanged += RecordResultChanged;
        _notices = notices;
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
        HistoryView.PinHeader(HomeViewport);
        // Set the width before the page loads, so a page with saved history does not open at the reading width.
        UpdateHomeWidth(_history.Snapshot().Count > 0);
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

    internal Task FinishBackgroundWorkAsync() => Task.WhenAll(_catalogNamesTask, _copyPicturesTask);

    internal void Dispose()
    {
        _disposed = true;
        _shortcut.RecordResultChanged -= RecordResultChanged;
        _history.Changed -= HistoryChanged;
        HistoryView.QueryChanged -= HistoryQueryChanged;
        HistoryView.CopyRequested -= CopyHistoryItem;
        HistoryView.RemoveRequested -= RemoveHistoryItem;
        HistoryView.UnpinHeader();
        _railPin?.Dispose();
        _railPin = null;
        _observer?.Dispose();
        _observer = null;
        _clock.Stop();
        if (_copiedTimer.IsEnabled) { _copiedTimer.Stop(); CopyLabel.Text = "Copy"; }
        _notices.Set(HistoryNoticeKey, null);
        _notices.Set(UsageNoticeKey, null);
        _notices.Set(ErrorNoticeKey, null);
    }

    private void RecordResultChanged()
    {
        if (!_disposed && IsLoaded) Render();
    }

    private void Render()
    {
        RenderRecordControl();
        RenderStatus();
        RenderClock();
        RenderHomeData();
        var payload = _state.Payload;
        var text = payload?.Text ?? _state.Transcript;
        if (TranscriptBox.Text != text) { TranscriptBox.Text = text; }
        var empty = string.IsNullOrWhiteSpace(text);
        var retainedInHistory = _state.TranscriptOperationId is { } operationId &&
            (_history.Contains(operationId) || operationId == _removedTranscriptOperationId);
        var hasMedia = payload is { Frames.Count: > 0 } || payload?.VideoPath is not null;
        var showTranscript = !empty && (!retainedInHistory || hasMedia);
        TranscriptCard.Visibility = showTranscript ? Visibility.Visible : Visibility.Collapsed;
        TranscriptHeader.Visibility = showTranscript ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = _state.IsIdle || _state.Phase == DictationPhase.RecoveryRequired;
        TranscriptTitle.Text = payload is null ? "Transcript" : "Dictation bundle";
        CopyPicturesButton.Visibility = payload is { Frames.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        CopyVideoButton.Visibility = payload?.VideoPath is not null ? Visibility.Visible : Visibility.Collapsed;
        CopyPicturesButton.IsEnabled = CopyButton.IsEnabled && !_copyingPictures;
        CopyVideoButton.IsEnabled = CopyButton.IsEnabled;
        var retained = _shortcut.RecordPayload;
        RetainedCaptureCard.Visibility = retained is not null && payload?.IsRecordTake != true ? Visibility.Visible : Visibility.Collapsed;
        RetainedCaptureHint.Text = $"Paste with {_state.Settings.RecordPasteShortcut.DisplayText}, or copy individual parts.";
        CopyRetainedTextButton.IsEnabled = CopyButton.IsEnabled;
        CopyRetainedPicturesButton.IsEnabled = CopyButton.IsEnabled && !_copyingPictures;
        CopyRetainedVideoButton.IsEnabled = CopyButton.IsEnabled;
        CopyRetainedPicturesButton.Visibility = retained is { Frames.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        CopyRetainedVideoButton.Visibility = retained?.VideoPath is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadHomeCatalogNamesAsync()
    {
        var dispatcher = DispatcherQueue;
        try
        {
            var entries = await NativeModelCatalog.LoadAsync(_paths.ModelCatalog).ConfigureAwait(false);
            var names = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.DisplayName))
                .ToDictionary(entry => entry.Id, HomeModelName.From, StringComparer.OrdinalIgnoreCase);
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

    // The catalog entry gives the plain name Speech models shows. An installed snapshot has no precision to drop,
    // so before the catalog loads, or without it, the snapshot's full name is the only name there is.
    private HomeModelName? HomeModelNameFor(string modelId)
    {
        if (_homeCatalogNames.TryGetValue(modelId, out var catalogName)) { return catalogName; }
        return _state.Models.FirstOrDefault(model => model.Id == modelId)?.DisplayName is { } name ? new HomeModelName(name, name) : null;
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
            ShortcutKeycaps.Show(ShortcutKeys, _homeShortcutDisplay);
        }
        if (!_homeDataInitialized || _homeDataDate != DateTime.Today || !ReferenceEquals(_homeUsage, _state.Usage) || _homeUsageError != _state.UsageError)
        {
            _homeDataInitialized = true;
            _homeDataDate = DateTime.Today;
            _homeUsage = _state.Usage;
            _homeUsageError = _state.UsageError;
            var insights = HomeInsightsProjection.Create(_state.Usage, _state.UsageError,
                HomeModelNameFor, DateOnly.FromDateTime(DateTime.Today), TimeZoneInfo.Local);
            // After a failed usage write a zero is not proof of no dictations, so History treats the count as unknown.
            _homeDictations = insights.UsageWarning is null ? insights.Dictations : null;
            HomeInsightsColumn.Render(insights);
            PostStorageNotice(UsageNoticeKey, "Some usage statistics are missing", insights.UsageWarning);
        }
        HistoryView.SetEmptyContext(_homeDictations);
    }

    private void RenderHomeHistory(bool animate)
    {
        var model = HomeHistoryProjection.Create(_history.Snapshot(), HistoryView.Query,
            _history.OversizedCurrentResult, HomeModelNameFor, DateTime.Today, _history.StorageError);
        HistoryView.Render(model, animate);
        UpdateHomeWidth(model.HasHistory);
        PostStorageNotice(HistoryNoticeKey, "History needs attention", model.StorageError);
    }

    // Storage problems are conditions, not events: the notice stays until the error clears and cannot be closed.
    private void PostStorageNotice(string key, string title, string? message) => _notices.Set(key,
        message is null ? null : new NoticeContent(NoticeSeverity.Warning, title, message, IsClosable: false), NoticeArea.Dictation);

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
            ShowError(error);
            return;
        }
        _notices.Set(ErrorNoticeKey, null);
        HistoryView.ShowCopied(item.OperationId);
    }

    // Renders synchronously so the view can move keyboard focus to the row that took the removed row's place.
    private void RemoveHistoryItem(HomeHistoryItem item)
    {
        if (!_history.Remove(item.OperationId)) { return; }
        if (item.OperationId == _state.TranscriptOperationId) { _removedTranscriptOperationId = item.OperationId; }
        RenderHomeHistory(animate: true);
    }

    private void DictateControlSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateShortcutLayout();
    }

    // Too narrow for one line, the buttons wrap under the hint and stay left-aligned so the sentence reads on. The
    // row gap exists only while they wrap: an empty second row must not add height under the button, or the header
    // centres the button above the title's line.
    private void UpdateShortcutLayout()
    {
        var stackedHelper = HomeViewport.ContentWidth < (double)Application.Current.Resources["ShortcutStackThreshold"];
        ShortcutControlGrid.RowSpacing = stackedHelper ? 8 : 0;
        Grid.SetRow(ShortcutButtons, stackedHelper ? 1 : 0);
        Grid.SetColumn(ShortcutButtons, stackedHelper ? 0 : 1);
        Grid.SetColumnSpan(ShortcutButtons, stackedHelper ? 2 : 1);
    }

    // Home is two columns only when History has a saved entry to fill one: an empty History beside the rail is a
    // near-empty column. Without entries (a search that matches nothing still has entries) the page keeps the reading
    // width and stacks, so the two insights cards do not stretch across a wide window. Runs on every history render,
    // so the first saved entry and the last removal switch the layout live.
    private void UpdateHomeWidth(bool hasHistory)
    {
        if (_homeHasHistory == hasHistory && HomeViewport.Wide == hasHistory) return;
        _homeHasHistory = hasHistory;
        HomeViewport.Wide = hasHistory;
        UpdateHomeColumns(HomeColumns.ActualWidth);
    }

    private void HomeColumnsSizeChanged(object sender, SizeChangedEventArgs args) => UpdateHomeColumns(args.NewSize.Width);

    // Wide: history beside a fixed insights rail that sticks at the history header's content top. History takes the
    // star row, so it is at least as tall as the rail and the rail can travel within it. Stacked (narrow, or no saved
    // history): history comes first, so its header, search, and empty state are in the first viewport, and the
    // insights cards follow it in a strip after the normal grid gap. Only the two-column rail is pinned.
    private void UpdateHomeColumns(double width)
    {
        var stacked = !_homeHasHistory || width < (double)Application.Current.Resources["HomeRailStackThreshold"];
        if (stacked) { _railPin?.Dispose(); _railPin = null; }
        else if (!_disposed)
        {
            // The rail top lines up with the stuck header's content and keeps the same gap above the visible bottom.
            var gap = HistoryView.StuckContentTop;
            _railPin ??= HomeViewport.Pin(HomeInsightsColumn, HistoryView, gap, gap);
        }
        if (HomeInsightsColumn.Strip == stacked && HomeColumns.ColumnDefinitions.Count == (stacked ? 1 : 2)) return;
        HomeInsightsColumn.Strip = stacked;
        HomeColumns.ColumnDefinitions.Clear();
        HomeColumns.RowDefinitions.Clear();
        HomeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!stacked) HomeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)Application.Current.Resources["HomeRailColumnWidth"] });
        HomeColumns.RowDefinitions.Add(new RowDefinition { Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        if (stacked) HomeColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(HomeInsightsColumn, stacked ? 0 : 1);
        Grid.SetRow(HomeInsightsColumn, stacked ? 1 : 0);
    }

    private void HomeViewportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateShortcutLayout();

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
            DictationPhase.Benchmarking => ("Measuring devices", "A device benchmark is running in Settings. Dictation starts when it finishes or is cancelled."),
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

    private async void RecordClicked(object sender, RoutedEventArgs args)
    {
        if (!IsEnabled) { return; }
        try
        {
            if (_state.CanFinish) { await _session.FinishAsync(); }
            else { await _session.StartDictationAsync(); }
            _notices.Set(ErrorNoticeKey, null);
        }
        catch (Exception error) { ShowError(error); }
    }
    private async void CancelClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            await _session.CancelAsync();
            _notices.Set(ErrorNoticeKey, null);
        }
        catch (Exception error) { ShowError(error); }
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
            _notices.Set(ErrorNoticeKey, null);
        }
        catch (Exception error) { ShowError(error); }
        finally { _picking = false; Render(); }
    }

    private void CopyClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var retainedCopy = ReferenceEquals(sender, CopyRetainedTextButton);
            var text = retainedCopy ? _shortcut.RecordPayload?.Text : _state.Payload?.Text ?? _state.Transcript;
            if (text is null) return;
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            if (retainedCopy) _notices.Confirm("Capture text copied.");
            else
            {
                CopyLabel.Text = "Copied";
                _copiedTimer.Stop();
                _copiedTimer.Start();
            }
            _notices.Set(ErrorNoticeKey, null);
        }
        catch (Exception error) { ShowError(error); }
    }

    private void CopyPicturesClicked(object sender, RoutedEventArgs args)
    {
        if (_disposed || !IsEnabled || !_copyPicturesTask.IsCompleted) return;
        var payload = ReferenceEquals(sender, CopyRetainedPicturesButton) ? _shortcut.RecordPayload : _state.Payload;
        if (payload is not { Frames.Count: > 0 }) return;
        _copyingPictures = true;
        _copyPicturesTask = CopyPicturesAsync(payload.Frames);
    }

    private async Task CopyPicturesAsync(IReadOnlyList<RecordFrame> frames)
    {
        Render();
        try
        {
            await WindowsRecordTakeFactory.CopyPicturesAsync(frames);
            if (!_disposed) _notices.Set(ErrorNoticeKey, null);
        }
        catch (Exception error) { if (!_disposed) ShowError(error); }
        finally
        {
            _copyingPictures = false;
            if (!_disposed) Render();
        }
    }

    private void CopyVideoClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var payload = ReferenceEquals(sender, CopyRetainedVideoButton) ? _shortcut.RecordPayload : _state.Payload;
            if (payload?.VideoPath is not { } path) return;
            var package = new DataPackage();
            package.SetText(new Uri(path).AbsoluteUri);
            Clipboard.SetContent(package);
            _notices.Set(ErrorNoticeKey, null);
        }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowError(Exception error) => _notices.Set(ErrorNoticeKey,
        new NoticeContent(NoticeSeverity.Error, "Operation failed", error.Message), NoticeArea.Dictation);
}
