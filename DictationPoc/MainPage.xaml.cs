using DictationPoc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace DictationPoc;

public sealed partial class MainPage : Page
{
    private readonly DictationSession _session;
    private readonly AppPaths _paths;
    private readonly Func<nint> _windowHandle;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private UiSessionObserver? _observer;
    private SessionSnapshot _state;
    private bool _rendering;
    private bool _picking;
    private IReadOnlyList<AudioModel>? _renderedModels;

    internal MainPage(DictationSession session, AppPaths paths, Func<nint> windowHandle)
    {
        InitializeComponent();
        _session = session;
        _paths = paths;
        _windowHandle = windowHandle;
        _state = session.State;
        _timer.Tick += (_, _) => DetailsText.Text =
            $"{(_state.Words is { } words ? $"{words} spoken words" : "Word count pending")} / {_session.Elapsed:mm\\:ss}";
        Loaded += (_, _) =>
        {
            _observer = new UiSessionObserver(session, DispatcherQueue, state => { _state = state; Render(); });
            _timer.Start();
        };
        Unloaded += (_, _) => { _observer?.Dispose(); _observer = null; _timer.Stop(); };
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            if (!ReferenceEquals(_renderedModels, _state.Models))
            {
                ModelBox.ItemsSource = _state.Models.Select(model => model.DisplayName ?? model.Id).ToArray();
                _renderedModels = _state.Models;
            }
            ModelBox.SelectedIndex = _state.SelectedIndex;
            ModelsFolderBox.Text = _state.ModelsDirectory;
            var idle = _state.IsIdle && !_picking;
            ModelsFolderBox.IsEnabled = idle;
            ConnectButton.IsEnabled = idle;
            ModelBox.IsEnabled = _state.Phase == DictationPhase.Ready && !_picking;
            LanguageBox.IsEnabled = ModelBox.IsEnabled;
            if (LanguageBox.FocusState == FocusState.Unfocused) { LanguageBox.Text = _state.Language; }
            BackendText.Text = _state.Phase == DictationPhase.Disconnected
                ? "Open Speech models to download verified weights or choose a model folder."
                : $"audio.cpp {_state.BackendVersion} / native CPU / {_state.Models.Count} installed models";
            ModelNote.Text = _state.SelectedModel?.Mode == "offline"
                ? "Offline model: verify a WAV recording. Large models require enough free memory."
                : _state.SelectedModel?.Preview == "final-only"
                    ? "This model accepts live audio but returns text after Finish."
                    : "Live preview is model-dependent. Replay requires bounded 16 kHz mono PCM16.";
            RecordButton.Content = _state.CanFinish ? "Finish dictation" : "Start dictation";
            RecordButton.IsEnabled = !_picking && (_state.CanStart || _state.CanFinish);
            FileButton.IsEnabled = ModelBox.IsEnabled;
            ReplayButton.IsEnabled = !_picking && _state.CanStart;
            SampleButton.IsEnabled = ModelBox.IsEnabled;
            CancelButton.IsEnabled = _state.CanCancel;
            CopyButton.IsEnabled = (_state.IsIdle || _state.Phase == DictationPhase.RecoveryRequired) &&
                !string.IsNullOrWhiteSpace(_state.Transcript);
            if (TranscriptBox.Text != _state.Transcript) { TranscriptBox.Text = _state.Transcript; }
            StatusInfo.Severity = _state.Notice.Kind switch
            {
                NoticeKind.Success => InfoBarSeverity.Success,
                NoticeKind.Warning => InfoBarSeverity.Warning,
                NoticeKind.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            };
            StatusInfo.Title = _state.Notice.Title;
            StatusInfo.Message = _state.Notice.Message;
            UsageToggle.IsOn = _state.Usage?.Enabled == true;
            UsageToggle.IsEnabled = idle && _state.Usage is not null;
            UsagePathText.Text = _session.UsagePath;
        }
        finally { _rendering = false; }
    }

    private async void ConnectClicked(object sender, RoutedEventArgs args) =>
        await RunCommandAsync(() => _session.ConnectAsync(_state.ModelsDirectory));
    private void ModelChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || ModelBox.SelectedIndex < 0) { return; }
        try { _session.SelectModel(ModelBox.SelectedIndex); }
        catch (Exception error) { _session.ReportUiError(error); }
    }
    private void LanguageChanged(object sender, TextChangedEventArgs args)
    {
        if (_rendering || LanguageBox is null) { return; }
        try { _session.SetLanguage(LanguageBox.Text); }
        catch (Exception error) { _session.ReportUiError(error); }
    }
    private async void RecordClicked(object sender, RoutedEventArgs args) =>
        await RunCommandAsync(() => _state.CanFinish ? _session.FinishAsync() : _session.StartDictationAsync());
    private async void CancelClicked(object sender, RoutedEventArgs args) => await RunCommandAsync(_session.CancelAsync);
    private async void FileClicked(object sender, RoutedEventArgs args) => await PickAndRunAsync(false);
    private async void ReplayClicked(object sender, RoutedEventArgs args) => await PickAndRunAsync(true);
    private async void SampleClicked(object sender, RoutedEventArgs args) => await RunCommandAsync(() =>
        _state.SelectedModel?.Mode == "streaming" ? _session.ReplayAsync(_paths.PublicSample) : _session.TranscribeFileAsync(_paths.PublicSample));

    private async Task PickAndRunAsync(bool replay)
    {
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
            var data = new DataPackage();
            data.SetText(_state.Transcript);
            Clipboard.SetContent(data);
            _session.Notify(NoticeKind.Success, "Copied", "The transcript is on the clipboard.");
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    private async void UsageToggled(object sender, RoutedEventArgs args)
    {
        if (!_rendering) { await RunCommandAsync(() => _session.SetUsageEnabledAsync(UsageToggle.IsOn)); }
    }

    private async Task RunCommandAsync(Func<Task> command)
    {
        try { await command(); }
        catch (Exception error) { _session.ReportUiError(error); }
    }
}
