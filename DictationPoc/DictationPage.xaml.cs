using DictationPoc.Core;
using Microsoft.UI.Text;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace DictationPoc;

public sealed partial class DictationPage : Page
{
    private static readonly TimeSpan CopiedDuration = TimeSpan.FromSeconds(2);

    private readonly DictationSession _session;
    private readonly AppPaths _paths;
    private readonly Func<nint> _windowHandle;
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _copiedTimer = new() { Interval = CopiedDuration };
    private SessionSnapshot _state;
    private UiSessionObserver? _observer;
    private IReadOnlyList<AudioModel>? _models;
    private SessionNotice? _dismissedNotice;
    private Visual? _innerVisual;
    private Visual? _outerVisual;
    private double _smoothedLevel;
    private bool _spinning;
    private bool _rendering;
    private bool _picking;

    internal DictationPage(DictationSession session, AppPaths paths, Func<nint> windowHandle)
    {
        InitializeComponent();
        _session = session;
        _paths = paths;
        _windowHandle = windowHandle;
        _state = session.State;
        _clock.Tick += (_, _) => RenderClock();
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); CopyLabel.Text = "Copy"; };
        Loaded += (_, _) =>
        {
            _observer = new UiSessionObserver(session, DispatcherQueue, state => { _state = state; Render(); }, OnLevel);
            _clock.Start();
        };
        Unloaded += (_, _) =>
        {
            _observer?.Dispose(); _observer = null;
            _clock.Stop();
            if (_copiedTimer.IsEnabled) { _copiedTimer.Stop(); CopyLabel.Text = "Copy"; }
            StopSpin();
            ResetHalos();
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
            ModelBox.IsEnabled = _state.Phase == DictationPhase.Ready && !_picking;
            ToolTipService.SetToolTip(ModelBox, ModelBox.IsEnabled ? ModelHint() : "Finish the current operation to change the model.");
            var ready = _state.Phase == DictationPhase.Ready && _state.SelectedModel is not null && !_picking;
            FileButton.Visibility = _state.SelectedModel is null ? Visibility.Collapsed : Visibility.Visible;
            FileButton.IsEnabled = ready;
            TranscribeItem.IsEnabled = ready;
            ReplayItem.IsEnabled = ready && _state.CanStart;
            RenderRecordControl();
            RenderStatus();
            RenderClock();
            RenderNotice();
            if (TranscriptBox.Text != _state.Transcript) { TranscriptBox.Text = _state.Transcript; }
            var empty = string.IsNullOrWhiteSpace(_state.Transcript);
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            CopyButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            CopyButton.IsEnabled = _state.IsIdle || _state.Phase == DictationPhase.RecoveryRequired;
        }
        finally { _rendering = false; }
    }

    private string? ModelHint() => _state.SelectedModel switch
    {
        { Mode: "offline" } => "Transcribes WAV files. No microphone dictation.",
        { Preview: "final-only" } => "Text appears after you finish.",
        { Preview: "buffered" } => "Text arrives in chunks while you speak.",
        { Preview: "live" } => "Text appears as you speak.",
        _ => null
    };

    private void RenderRecordControl()
    {
        var busy = _state.Phase is DictationPhase.Connecting or DictationPhase.Preparing or DictationPhase.Finishing
            or DictationPhase.Transcribing or DictationPhase.Cancelling or DictationPhase.MaintainingModels
            or DictationPhase.Closing;
        RecordButton.IsEnabled = !_picking && (_state.CanStart || _state.CanFinish);
        RecordGlyph.Glyph = "";
        var label = _state.CanFinish ? "Finish dictation" : "Start dictation";
        AutomationProperties.SetName(RecordButton, label);
        ToolTipService.SetToolTip(RecordButton, label);
        if (busy) { StartSpin(); } else { StopSpin(); }
        if (_state.Phase == DictationPhase.Recording && !_state.IsReplay) { ShowHalos(); }
        else { ResetHalos(); }
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
            DictationPhase.Ready => ("File transcription only", "Choose a WAV file, or pick a streaming model to dictate."),
            DictationPhase.Preparing => ("Preparing", "Loading the model before audio starts."),
            DictationPhase.Recording when _state.IsReplay => ("Replaying WAV", "No microphone is open."),
            DictationPhase.Recording => ("Listening", preview switch
            {
                "live" => "Text appears as you speak.",
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
        StatusTitle.Text = title;
        StatusDetail.Inlines.Clear();
        if (detail.Length > 0) { StatusDetail.Inlines.Add(new Run { Text = detail }); return; }
        StatusDetail.Inlines.Add(new Run { Text = settings.PushToTalk ? "Click the microphone, or hold " : "Click the microphone, or press " });
        StatusDetail.Inlines.Add(new Run { Text = settings.Shortcut.DisplayText, FontWeight = FontWeights.SemiBold });
        StatusDetail.Inlines.Add(new Run { Text = settings.PushToTalk ? " in any app and release to finish." : " in any app, then press it again to finish." });
    }

    // The live row keeps its height in every state so starting a dictation does not shift the layout.
    private void RenderClock()
    {
        var live = _state.CanCancel;
        var completed = _state.IsIdle && _state.Result is not null;
        LiveRow.Opacity = live || completed ? 1 : 0;
        LiveRow.IsHitTestVisible = live;
        CancelButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = live;
        if (!live && !completed) { return; }
        var text = _session.Elapsed.ToString(@"mm\:ss");
        if (_state.Words is { } words) { text += $"  ·  {words:N0} {(words == 1 ? "word" : "words")}"; }
        LiveText.Text = text;
    }

    // Only warnings and errors interrupt. Success and information are already reflected by the hero status,
    // the transcript, or the control that was used.
    private void RenderNotice()
    {
        var notice = _state.Notice;
        var attention = notice.Kind is NoticeKind.Warning or NoticeKind.Error;
        if (!attention || ReferenceEquals(notice, _dismissedNotice)) { StatusInfo.IsOpen = false; return; }
        StatusInfo.Severity = notice.Kind == NoticeKind.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        StatusInfo.Title = notice.Title;
        StatusInfo.Message = notice.Message;
        StatusInfo.IsOpen = true;
    }

    private void StatusInfoClosing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton) { _dismissedNotice = _state.Notice; }
    }

    // The ring is a thin arc that rotates around the button. With animations off it stays as a static arc.
    private void StartSpin()
    {
        SpinRing.Opacity = 1;
        if (_spinning || !_uiSettings.AnimationsEnabled) { return; }
        _spinning = true;
        SpinStoryboard.Begin();
    }

    private void StopSpin()
    {
        SpinRing.Opacity = 0;
        if (!_spinning) { return; }
        _spinning = false;
        SpinStoryboard.Stop();
    }

    // Halo scale uses Composition implicit animations, so each level update glides over 90 ms instead of stepping.
    private Visual HaloVisual(Ellipse halo, ref Visual? cache)
    {
        if (cache is not null) { return cache; }
        var visual = ElementCompositionPreview.GetElementVisual(halo);
        var compositor = visual.Compositor;
        var glide = compositor.CreateVector3KeyFrameAnimation();
        glide.Target = "Scale";
        glide.InsertExpressionKeyFrame(1f, "this.FinalValue");
        glide.Duration = TimeSpan.FromMilliseconds(90);
        var implicitAnimations = compositor.CreateImplicitAnimationCollection();
        implicitAnimations["Scale"] = glide;
        visual.ImplicitAnimations = implicitAnimations;
        visual.CenterPoint = new Vector3((float)halo.Width / 2, (float)halo.Height / 2, 0);
        cache = visual;
        return visual;
    }

    private void SetHalo(double inner, double outer)
    {
        HaloVisual(InnerHalo, ref _innerVisual).Scale = new Vector3((float)inner, (float)inner, 1);
        HaloVisual(OuterHalo, ref _outerVisual).Scale = new Vector3((float)outer, (float)outer, 1);
    }

    private void ShowHalos()
    {
        InnerHalo.Opacity = 0.28;
        OuterHalo.Opacity = 0.12;
        if (!_uiSettings.AnimationsEnabled) { SetHalo(1.15, 1.15); }
        else if (_smoothedLevel == 0) { SetHalo(1, 1); }
    }

    private void ResetHalos()
    {
        _smoothedLevel = 0;
        InnerHalo.Opacity = 0;
        OuterHalo.Opacity = 0;
        SetHalo(1, 1);
    }

    private void OnLevel(double raw)
    {
        if (_state.Phase != DictationPhase.Recording || _state.IsReplay || !_uiSettings.AnimationsEnabled) { return; }
        var target = FloatingPreviewPresentation.MeterLevel(raw);
        _smoothedLevel += (target - _smoothedLevel) * (target > _smoothedLevel ? 0.5 : 0.15);
        SetHalo(1.0 + 0.6 * _smoothedLevel, 1.0 + 1.0 * _smoothedLevel);
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
    private async void TranscribeClicked(object sender, RoutedEventArgs args) => await PickAndRunAsync(replay: false);
    private async void ReplayClicked(object sender, RoutedEventArgs args) => await PickAndRunAsync(replay: true);

    private async Task PickAndRunAsync(bool replay)
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
            if (file is not null)
            {
                if (replay) { await _session.ReplayAsync(file.Path); }
                else { await _session.TranscribeFileAsync(file.Path); }
            }
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
