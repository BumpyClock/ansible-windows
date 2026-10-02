using DictationPoc.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace DictationPoc;

public sealed partial class MainPage : Page
{
    private readonly DictationController _controller;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _rendering;
    private IReadOnlyList<Core.AudioModel>? _renderedModels;

    public MainPage()
    {
        InitializeComponent();
        _controller = ((App)Application.Current).Controller;
        _timer.Tick += (_, _) => DetailsText.Text = $"{_controller.Words} words / {_controller.Elapsed:mm\\:ss}";
        Loaded += (_, _) =>
        {
            _controller.Changed += Render;
            _controller.UsageChanged += Render;
            _timer.Start();
            Render();
        };
        Unloaded += (_, _) =>
        {
            _controller.Changed -= Render;
            _controller.UsageChanged -= Render;
            _timer.Stop();
        };
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            if (!ReferenceEquals(_renderedModels, _controller.Models))
            {
                ModelBox.ItemsSource = _controller.Models.Select(model => model.DisplayName ?? model.Id).ToArray();
                _renderedModels = _controller.Models;
            }
            ModelBox.SelectedIndex = _controller.SelectedIndex;
            if (ModelsFolderBox.Text != _controller.ModelsDirectory && !ModelsFolderBox.FocusState.HasFlag(FocusState.Keyboard))
            {
                ModelsFolderBox.Text = _controller.ModelsDirectory;
            }
            var idle = _controller.IsIdle;
            ModelsFolderBox.IsEnabled = idle;
            ConnectButton.IsEnabled = idle;
            ModelBox.IsEnabled = _controller.Phase == DictationPhase.Ready;
            LanguageBox.IsEnabled = ModelBox.IsEnabled;
            BackendText.Text = _controller.Phase == DictationPhase.Disconnected
                ? "Build the native DLL and install verified models, then load this folder."
                : $"audio.cpp {_controller.BackendVersion} / C ABI 0.2 / CPU / {_controller.Models.Count} models";
            ModelNote.Text = _controller.SelectedModel?.Mode == "offline"
                ? "Offline model: verify a WAV recording. Large models require enough free memory."
                : _controller.SelectedModel?.Preview == "final-only"
                    ? "This model accepts live audio but returns text after Finish. No preview is simulated."
                    : "Live preview is model-dependent. WAV replay requires 16 kHz mono PCM16.";
            RecordButton.Content = _controller.CanFinish && !_controller.IsReplay ? "Finish dictation" : "Start dictation";
            RecordButton.IsEnabled = _controller.CanStart || _controller.CanFinish && !_controller.IsReplay;
            FileButton.IsEnabled = _controller.Phase == DictationPhase.Ready;
            ReplayButton.IsEnabled = _controller.CanStart;
            SampleButton.IsEnabled = _controller.Phase == DictationPhase.Ready;
            CancelButton.IsEnabled = _controller.CanCancel;
            CopyButton.IsEnabled = idle && !string.IsNullOrWhiteSpace(_controller.Transcript);
            if (TranscriptBox.Text != _controller.Transcript) { TranscriptBox.Text = _controller.Transcript; }
            StatusInfo.Severity = _controller.Notice.Kind switch
            {
                NoticeKind.Success => InfoBarSeverity.Success,
                NoticeKind.Warning => InfoBarSeverity.Warning,
                NoticeKind.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            };
            StatusInfo.Title = _controller.Notice.Title;
            StatusInfo.Message = _controller.Notice.Message;
            UsageToggle.IsOn = _controller.Usage?.Enabled == true;
            UsageToggle.IsEnabled = _controller.Usage is not null;
            UsagePathText.Text = Path.Combine(AppContext.BaseDirectory, "usage.json");
        }
        finally { _rendering = false; }
    }

    private async void ConnectClicked(object sender, RoutedEventArgs args) => await _controller.ConnectAsync(ModelsFolderBox.Text);
    private void ModelChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_rendering && ModelBox.SelectedIndex >= 0) { _controller.SelectModel(ModelBox.SelectedIndex); }
    }
    private void LanguageChanged(object sender, TextChangedEventArgs args)
    {
        if (!_rendering && LanguageBox is not null) { _controller.Language = LanguageBox.Text; }
    }
    private async void RecordClicked(object sender, RoutedEventArgs args)
    {
        if (_controller.CanFinish) { await _controller.FinishAsync(); }
        else { await _controller.StartDictationAsync(); }
    }
    private async void CancelClicked(object sender, RoutedEventArgs args) => await _controller.CancelAsync();
    private async void FileClicked(object sender, RoutedEventArgs args) => await PickAndRunAsync(false);
    private async void ReplayClicked(object sender, RoutedEventArgs args) => await PickAndRunAsync(true);
    private async void SampleClicked(object sender, RoutedEventArgs args)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "validation-sample.wav");
        if (!File.Exists(path))
        {
            _controller.Fail(new FileNotFoundException("The public validation sample is missing from the app package."));
            return;
        }
        if (_controller.SelectedModel?.Mode == "streaming") { await _controller.ReplayAsync(path); }
        else { await _controller.TranscribeFileAsync(path); }
    }

    private async Task PickAndRunAsync(bool replay)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wav");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).Window!));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                if (replay) { await _controller.ReplayAsync(file.Path); }
                else { await _controller.TranscribeFileAsync(file.Path); }
            }
        }
        catch (Exception error) { _controller.Fail(error); }
    }

    private void CopyClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(_controller.Transcript);
            Clipboard.SetContent(data);
            _controller.Notify(NoticeKind.Success, "Copied", "The transcript is on the clipboard.");
        }
        catch (Exception error) { _controller.Fail(error); }
    }

    private async void UsageToggled(object sender, RoutedEventArgs args)
    {
        if (!_rendering) { await _controller.SetUsageEnabledAsync(UsageToggle.IsOn); }
    }
}
