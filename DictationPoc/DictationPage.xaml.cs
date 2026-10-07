using DictationPoc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DictationPoc;

public sealed partial class DictationPage : Page
{
    private readonly DictationSession _session;
    private SessionSnapshot _state;
    private UiSessionObserver? _observer;
    private IReadOnlyList<AudioModel>? _models;
    private bool _rendering;

    internal DictationPage(DictationSession session)
    {
        InitializeComponent();
        _session = session;
        _state = session.State;
        Loaded += (_, _) => _observer = new UiSessionObserver(session, DispatcherQueue, state => { _state = state; Render(); });
        Unloaded += (_, _) => { _observer?.Dispose(); _observer = null; };
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
            RecordButton.IsEnabled = _state.CanStart || _state.CanFinish;
            RecordButton.Content = _state.CanFinish ? "Finish dictation" : "Start dictation";
            CancelButton.IsEnabled = _state.CanCancel;
            CopyButton.IsEnabled = (_state.IsIdle || _state.Phase == DictationPhase.RecoveryRequired) &&
                !string.IsNullOrWhiteSpace(_state.Transcript);
            ModelNote.Text = _state.SelectedModel?.Mode == "offline"
                ? "WAV transcription only. Open Recognition tools in Settings."
                : _state.SelectedModel?.Preview == "final-only"
                    ? "Text appears after Finish dictation."
                    : _state.SelectedModel?.Preview == "buffered"
                        ? "Buffered recognition. Text updates are not continuous."
                        : "";
            ModelNote.Visibility = ModelNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            StatusInfo.Title = _state.Notice.Title;
            StatusInfo.Message = _state.Notice.Message;
            StatusInfo.IsOpen = _state.Phase != DictationPhase.Ready || _state.Notice.Kind != NoticeKind.Information;
            StatusInfo.Severity = _state.Notice.Kind switch
            {
                NoticeKind.Success => InfoBarSeverity.Success,
                NoticeKind.Warning => InfoBarSeverity.Warning,
                NoticeKind.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            };
            if (TranscriptBox.Text != _state.Transcript) { TranscriptBox.Text = _state.Transcript; }
        }
        finally { _rendering = false; }
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
