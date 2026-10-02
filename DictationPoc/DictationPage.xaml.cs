using DictationPoc.Core;
using DictationPoc.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DictationPoc;

public sealed partial class DictationPage : Page
{
    private readonly DictationController _controller;
    private IReadOnlyList<AudioModel>? _models;
    private bool _rendering;

    public DictationPage()
    {
        InitializeComponent();
        _controller = ((App)Application.Current).Controller;
        Loaded += (_, _) => { _controller.Changed += Render; Render(); };
        Unloaded += (_, _) => _controller.Changed -= Render;
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            if (!ReferenceEquals(_models, _controller.Models))
            {
                ModelBox.ItemsSource = _controller.Models.Select(model => model.DisplayName ?? model.Id).ToArray();
                _models = _controller.Models;
            }
            ModelBox.SelectedIndex = _controller.SelectedIndex;
            ModelBox.IsEnabled = _controller.Phase == DictationPhase.Ready;
            RecordButton.IsEnabled = _controller.CanStart || _controller.CanFinish && !_controller.IsReplay;
            RecordButton.Content = _controller.CanFinish && !_controller.IsReplay ? "Finish dictation" : "Start dictation";
            CancelButton.IsEnabled = _controller.CanCancel;
            CopyButton.IsEnabled = _controller.IsIdle && !string.IsNullOrWhiteSpace(_controller.Transcript);
            ModelNote.Text = _controller.SelectedModel?.Mode == "offline"
                ? "This model is for file transcription. Open Settings + verification to test it."
                : _controller.SelectedModel?.Preview == "final-only"
                    ? "This adapter returns text after Finish. The waveform still shows your real microphone input."
                    : "Live transcript timing depends on the model and your hardware.";
            StatusInfo.Title = _controller.Notice.Title;
            StatusInfo.Message = _controller.Notice.Message;
            StatusInfo.Severity = _controller.Notice.Kind switch
            {
                NoticeKind.Success => InfoBarSeverity.Success,
                NoticeKind.Warning => InfoBarSeverity.Warning,
                NoticeKind.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            };
            if (TranscriptBox.Text != _controller.Transcript) { TranscriptBox.Text = _controller.Transcript; }
        }
        finally { _rendering = false; }
    }

    private void ModelChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_rendering && ModelBox.SelectedIndex >= 0) { _controller.SelectModel(ModelBox.SelectedIndex); }
    }
    private async void RecordClicked(object sender, RoutedEventArgs args)
    {
        if (_controller.CanFinish) { await _controller.FinishAsync(); }
        else { await _controller.StartDictationAsync(); }
    }
    private async void CancelClicked(object sender, RoutedEventArgs args) => await _controller.CancelAsync();
    private void CopyClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(_controller.Transcript);
            Clipboard.SetContent(package);
            _controller.Notify(NoticeKind.Success, "Copied", "The transcript is ready to paste.");
        }
        catch (Exception error) { _controller.Fail(error); }
    }
}
