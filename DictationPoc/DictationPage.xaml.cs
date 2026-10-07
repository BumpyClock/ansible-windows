using DictationPoc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.ViewManagement;

namespace DictationPoc;

public sealed partial class DictationPage : Page
{
    private const int LevelBarCount = 24;
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(6);

    private readonly DictationSession _session;
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = NoticeDuration };
    private readonly List<Rectangle> _levelBars = [];
    private readonly double[] _levels = new double[LevelBarCount];
    private SessionSnapshot _state;
    private UiSessionObserver? _observer;
    private IReadOnlyList<AudioModel>? _models;
    private SessionNotice? _surfacedNotice;
    private SessionNotice? _dismissedNotice;
    private string? _renderedShortcut;
    private bool _pulsing;
    private bool _rendering;

    internal DictationPage(DictationSession session)
    {
        InitializeComponent();
        _session = session;
        _state = session.State;
        for (var index = 0; index < LevelBarCount; index++)
        {
            var bar = new Rectangle
            {
                Width = 3, Height = 3, RadiusX = 1.5, RadiusY = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["PreviewWaveformBarStyle"]
            };
            _levelBars.Add(bar);
            LevelBars.Children.Add(bar);
        }
        _clock.Tick += (_, _) => RenderClock();
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); StatusInfo.IsOpen = false; };
        Loaded += (_, _) =>
        {
            _observer = new UiSessionObserver(session, DispatcherQueue, state => { _state = state; Render(); }, UpdateLevel);
            _clock.Start();
        };
        Unloaded += (_, _) =>
        {
            _observer?.Dispose(); _observer = null;
            _clock.Stop();
            _noticeTimer.Stop();
            StopPulse();
        };
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            if (!ReferenceEquals(_models, _state.Models))
            {
                ModelBox.ItemsSource = _state.Models.Select(model => model.DisplayName ?? model.Id).ToArray();
                _models = _state.Models;
            }
            ModelBox.SelectedIndex = _state.SelectedIndex;
            ModelBox.IsEnabled = _state.Phase == DictationPhase.Ready;
            RenderRecordControl();
            RenderStatus();
            RenderShortcut();
            RenderClock();
            RenderNotice();
            ModelNote.Text = _state.SelectedModel?.Mode == "offline"
                ? "File transcription only. Use Recognition tools in Settings."
                : _state.SelectedModel?.Preview == "final-only"
                    ? "Text appears after you finish."
                    : _state.SelectedModel?.Preview == "buffered"
                        ? "Buffered recognition. Text arrives in chunks."
                        : "";
            ModelNote.Visibility = ModelNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            CancelButton.IsEnabled = _state.CanCancel;
            CancelButton.Visibility = _state.CanCancel ? Visibility.Visible : Visibility.Collapsed;
            CopyButton.IsEnabled = (_state.IsIdle || _state.Phase == DictationPhase.RecoveryRequired) &&
                !string.IsNullOrWhiteSpace(_state.Transcript);
            if (TranscriptBox.Text != _state.Transcript) { TranscriptBox.Text = _state.Transcript; }
            var empty = string.IsNullOrWhiteSpace(_state.Transcript);
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Text = _state.Phase == DictationPhase.Recording
                ? _state.SelectedModel?.Preview == "live"
                    ? "Listening. Text appears as the model recognizes speech."
                    : "Listening. Text appears after you finish."
                : "Recognized text appears here. Transcripts stay in memory on this device.";
        }
        finally { _rendering = false; }
    }

    private void RenderRecordControl()
    {
        var busy = _state.Phase is DictationPhase.Connecting or DictationPhase.Preparing or DictationPhase.Finishing
            or DictationPhase.Transcribing or DictationPhase.Cancelling or DictationPhase.MaintainingModels
            or DictationPhase.Closing;
        BusyRing.IsActive = busy;
        RecordButton.IsEnabled = _state.CanStart || _state.CanFinish;
        RecordGlyph.Glyph = _state.CanFinish ? "" : "";
        var label = _state.CanFinish ? "Finish dictation" : "Start dictation";
        AutomationProperties.SetName(RecordButton, label);
        ToolTipService.SetToolTip(RecordButton, label);
        var recording = _state.Phase == DictationPhase.Recording;
        if (recording && _uiSettings.AnimationsEnabled) { StartPulse(); }
        else { StopPulse(); }
        if (!recording) { Array.Clear(_levels); PaintLevels(); }
    }

    private void RenderStatus()
    {
        var preview = _state.SelectedModel?.Preview;
        var (title, detail) = _state.Phase switch
        {
            DictationPhase.Disconnected => ("No speech model loaded",
                _state.Models.Count == 0 ? "Download a model in Speech models to start." : _state.Notice.Message),
            DictationPhase.Connecting => ("Loading models", "Opening the native backend."),
            DictationPhase.Ready when _state.CanStart => ("Ready to dictate",
                "Click the microphone to dictate here, or use the shortcut in any app."),
            DictationPhase.Ready when _state.SelectedModel is null => ("Choose a speech model", "Pick an installed model to begin."),
            DictationPhase.Ready => ("File transcription only",
                "This model does not take microphone input. Choose a streaming model to dictate."),
            DictationPhase.Preparing => ("Preparing", "Loading the model before audio starts."),
            DictationPhase.Recording when _state.IsReplay => ("Replaying WAV", "No microphone is open."),
            DictationPhase.Recording => ("Listening", preview switch
            {
                "live" => "Text appears as you speak. Click stop when you are done.",
                "buffered" => "Text arrives in chunks. Click stop when you are done.",
                _ => "Text appears after you finish. Click stop when you are done."
            }),
            DictationPhase.Finishing => ("Finishing", "Recognizing the last audio."),
            DictationPhase.Transcribing => ("Transcribing file", _state.Notice.Message),
            DictationPhase.Cancelling => ("Cancelling", "Releasing the microphone and model."),
            DictationPhase.MaintainingModels => ("Updating models", _state.Notice.Message),
            DictationPhase.RecoveryRequired => ("Recovery required", _state.Notice.Message),
            _ => ("Closing", "Releasing native resources.")
        };
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
    }

    private void RenderShortcut()
    {
        var settings = _state.Settings;
        ShortcutVerb.Text = settings.PushToTalk ? "Hold" : "Press";
        ShortcutSuffix.Text = settings.PushToTalk ? "in any app, release to finish" : "in any app, again to finish";
        var display = settings.Shortcut.DisplayText;
        if (display == _renderedShortcut) { return; }
        _renderedShortcut = display;
        ShortcutKeys.Children.Clear();
        var keys = display.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < keys.Length; index++)
        {
            if (index > 0)
            {
                ShortcutKeys.Children.Add(new TextBlock
                {
                    Text = "+", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)Application.Current.Resources["SecondaryTextStyle"]
                });
            }
            ShortcutKeys.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["KeycapStyle"],
                Child = new TextBlock { Text = keys[index], Style = (Style)Application.Current.Resources["KeycapTextStyle"] }
            });
        }
        AutomationProperties.SetName(ShortcutRow, $"{ShortcutVerb.Text} {display} {ShortcutSuffix.Text}");
    }

    private void RenderClock()
    {
        var live = _state.Activity is SessionActivity.Dictation or SessionActivity.Replay &&
            _state.Phase is DictationPhase.Preparing or DictationPhase.Recording or DictationPhase.Finishing;
        var completed = _state.IsIdle && _state.Result is not null;
        LiveRow.Visibility = live || completed ? Visibility.Visible : Visibility.Collapsed;
        if (LiveRow.Visibility == Visibility.Collapsed) { return; }
        ElapsedText.Text = _session.Elapsed.ToString(@"mm\:ss");
        WordsText.Text = _state.Words is { } words
            ? $"{words:N0} {(words == 1 ? "word" : "words")}"
            : live ? "Counting words" : "Word count unknown";
        LevelBars.Visibility = _state.Phase == DictationPhase.Recording && !_state.IsReplay
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // Warnings and errors stay until dismissed. Success and information notices are shown once for a few
    // seconds while the page is idle; during live work the hero status already describes the state.
    private void RenderNotice()
    {
        var notice = _state.Notice;
        var attention = notice.Kind is NoticeKind.Warning or NoticeKind.Error;
        StatusInfo.Severity = notice.Kind switch
        {
            NoticeKind.Success => InfoBarSeverity.Success,
            NoticeKind.Warning => InfoBarSeverity.Warning,
            NoticeKind.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational
        };
        if (ReferenceEquals(notice, _dismissedNotice)) { StatusInfo.IsOpen = false; return; }
        if (ReferenceEquals(notice, _surfacedNotice)) { return; }
        _surfacedNotice = notice;
        _noticeTimer.Stop();
        StatusInfo.Title = notice.Title;
        StatusInfo.Message = notice.Message;
        if (attention)
        {
            StatusInfo.IsOpen = true;
        }
        else if (_state.IsIdle || _state.Phase == DictationPhase.RecoveryRequired)
        {
            StatusInfo.IsOpen = true;
            _noticeTimer.Start();
        }
        else { StatusInfo.IsOpen = false; }
    }

    private void StatusInfoClosing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton) { _dismissedNotice = _surfacedNotice; }
        _noticeTimer.Stop();
    }

    private void StartPulse()
    {
        if (_pulsing) { return; }
        _pulsing = true;
        PulseStoryboard.Begin();
    }

    private void StopPulse()
    {
        if (!_pulsing) { return; }
        _pulsing = false;
        PulseStoryboard.Stop();
        PulseRing.Opacity = 0;
    }

    private void UpdateLevel(double level)
    {
        if (_state.Phase != DictationPhase.Recording || _state.IsReplay) { return; }
        level = FloatingPreviewPresentation.MeterLevel(level);
        if (!_uiSettings.AnimationsEnabled) { Array.Fill(_levels, level); }
        else
        {
            Array.Copy(_levels, 1, _levels, 0, _levels.Length - 1);
            _levels[^1] = level;
        }
        PaintLevels();
    }

    private void PaintLevels()
    {
        for (var index = 0; index < _levelBars.Count; index++) { _levelBars[index].Height = 3 + _levels[index] * 25; }
    }

    private void ModelChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || ModelBox.SelectedIndex < 0) { return; }
        try { _session.SelectModel(ModelBox.SelectedIndex); }
        catch (Exception error) { _session.ReportUiError(error); }
    }
    private async void RecordClicked(object sender, RoutedEventArgs args)
    {
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
    private void CopyClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(_state.Transcript);
            Clipboard.SetContent(package);
            _session.Notify(NoticeKind.Success, "Copied", "The transcript is ready to paste.");
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }
}
