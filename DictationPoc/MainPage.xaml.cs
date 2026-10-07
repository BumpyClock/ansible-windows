using DictationPoc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace DictationPoc;

public sealed partial class MainPage : Page
{
    private readonly DictationSession _session;
    private readonly AppPaths _paths;
    private readonly Func<nint> _windowHandle;
    private readonly Action<DictationShortcut> _changeHotkey;
    private readonly Action<bool> _changeMode;
    private readonly Action<bool> _captureShortcut;
    private bool _recordingShortcut;
    private DictationShortcut? _pendingShortcut;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private UiSessionObserver? _observer;
    private SessionSnapshot _state;
    private bool _rendering;
    private bool _picking;
    private bool _dictionaryDirty;
    private IReadOnlyList<AudioModel>? _renderedModels;

    internal MainPage(DictationSession session, AppPaths paths, Func<nint> windowHandle,
        Action<DictationShortcut> changeHotkey, Action<bool> changeMode, Action<bool> captureShortcut)
    {
        _rendering = true;
        InitializeComponent();
        _session = session;
        _paths = paths;
        _windowHandle = windowHandle;
        _changeHotkey = changeHotkey;
        _changeMode = changeMode;
        _captureShortcut = captureShortcut;
        _state = session.State;
        const string typingHelp = "Live models type recognition updates as they arrive. Held Ctrl, Alt, Shift or Win keys delay typing until released. Some editors cannot distinguish fields within the same window.";
        ToolTipService.SetToolTip(HotkeyCaptureBox, typingHelp);
        AutomationProperties.SetHelpText(HotkeyCaptureBox, typingHelp);
        _rendering = true;
        HotkeyCaptureBox.Text = _state.Settings.Shortcut.DisplayText;
        PushToTalkToggle.IsOn = _state.Settings.PushToTalk;
        _rendering = false;
        _timer.Tick += (_, _) => DetailsText.Text =
            $"{(_state.Words is { } words ? $"{words} spoken words" : "Word count pending")} / {_session.Elapsed:mm\\:ss}";
        Loaded += (_, _) =>
        {
            _observer = new UiSessionObserver(session, DispatcherQueue, state => { _state = state; Render(); });
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            EndShortcutCapture();
            _observer?.Dispose(); _observer = null; _timer.Stop();
        };
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
            if (!_dictionaryDirty) { DictionaryBox.Text = _state.Settings.CustomDictionary; }
            DictionaryBox.IsEnabled = idle;
            SaveDictionaryButton.IsEnabled = idle && _dictionaryDirty;
            DictionaryDraftText.Visibility = _dictionaryDirty ? Visibility.Visible : Visibility.Collapsed;
            DictionarySupportText.Text = _state.SelectedModel is not { } selected
                ? "Choose a model to see whether dictionary hints are supported. Your list stays on this device."
                : selected.SupportsCustomDictionary
                    ? selected.Mode == "offline"
                        ? $"{selected.DisplayName ?? selected.Id} uses the saved dictionary as context for WAV transcription."
                        : $"{selected.DisplayName ?? selected.Id} uses the saved dictionary as context for file and streaming recognition."
                    : $"{selected.DisplayName ?? selected.Id} does not support dictionary hints. Your list is kept for supported models.";
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
            StatusInfo.IsOpen = _state.Phase != DictationPhase.Ready || _state.Notice.Kind != NoticeKind.Information;
            UsageToggle.IsOn = _state.Settings.CollectUsage;
            UsageToggle.IsEnabled = idle;
            UsagePathText.Text = _session.UsagePath;
            ToolTipService.SetToolTip(UsagePathText, _session.UsagePath);
            if (!_recordingShortcut) { HotkeyCaptureBox.Text = _state.Settings.Shortcut.DisplayText; }
            ChangeShortcutButton.IsEnabled = idle;
            PushToTalkToggle.IsEnabled = idle;
            PushToTalkToggle.IsOn = _state.Settings.PushToTalk;
            var shortcut = _state.Settings.Shortcut;
            ShortcutHint.Text = !_state.Settings.PushToTalk
                ? $"Press {shortcut.DisplayText} to dictate into the focused app; press again to finish."
                : shortcut.Modifiers == 0
                    ? $"Hold {shortcut.DisplayText} to dictate into the focused app; release to finish."
                    : $"Hold {shortcut.DisplayText} to record. Text is inserted after release.";
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
    private void DictionaryChanged(object sender, TextChangedEventArgs args)
    {
        if (_rendering || DictionaryBox is null) { return; }
        var draft = DictionaryBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        _dictionaryDirty = draft != _session.State.Settings.CustomDictionary;
        SaveDictionaryButton.IsEnabled = _session.State.IsIdle && !_picking && _dictionaryDirty;
        DictionaryDraftText.Visibility = _dictionaryDirty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SaveDictionaryClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            _session.SetCustomDictionary(DictionaryBox.Text);
            _state = _session.State;
            if (_state.Notice.Kind == NoticeKind.Success) { _dictionaryDirty = false; }
        }
        catch (Exception error) { _session.ReportUiError(error); _state = _session.State; }
        Render();
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

    private void UsageToggled(object sender, RoutedEventArgs args)
    {
        if (_rendering) { return; }
        try { _session.SetUsageEnabled(UsageToggle.IsOn); }
        catch (Exception error) { _session.ReportUiError(error); Render(); }
    }

    private void DictationModeChanged(object sender, RoutedEventArgs args)
    {
        if (_rendering) { return; }
        try { _changeMode(PushToTalkToggle.IsOn); }
        catch (Exception error) { _session.ReportUiError(error); Render(); }
    }

    private void ChangeShortcutClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            _captureShortcut(true);
            _recordingShortcut = true;
            _pendingShortcut = null;
            HotkeyCaptureBox.Text = "Press a shortcut; Esc cancels";
            HotkeyCaptureBox.Focus(FocusState.Programmatic);
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    private void ShortcutKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_recordingShortcut) { return; }
        args.Handled = true;
        if (args.Key == VirtualKey.Escape) { EndShortcutCapture(); return; }
        if (args.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            return;
        uint modifiers = 0;
        if (Down(VirtualKey.Menu)) { modifiers |= 1; }
        if (Down(VirtualKey.Control)) { modifiers |= 2; }
        if (Down(VirtualKey.Shift)) { modifiers |= 4; }
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) { modifiers |= 8; }
        var shortcut = new DictationShortcut(modifiers, (uint)args.Key);
        if (!shortcut.IsValid)
        {
            HotkeyCaptureBox.Text = "Use F1-F11, or Ctrl/Alt/Win with a letter or digit";
            return;
        }
        _pendingShortcut = shortcut;
        HotkeyCaptureBox.Text = $"{shortcut.DisplayText} (release to apply)";
    }

    private void ShortcutKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (!_recordingShortcut || _pendingShortcut is not { } shortcut || (uint)args.Key != shortcut.Key) { return; }
        args.Handled = true;
        try { _changeHotkey(shortcut); }
        catch (Exception error) { _session.Notify(NoticeKind.Error, "Shortcut unavailable", error.Message); }
        finally { EndShortcutCapture(); }
    }

    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void ShortcutLostFocus(object sender, RoutedEventArgs args) => EndShortcutCapture();

    private void EndShortcutCapture()
    {
        if (!_recordingShortcut) { return; }
        _recordingShortcut = false;
        _pendingShortcut = null;
        try { _captureShortcut(false); }
        catch (Exception error) { _session.Notify(NoticeKind.Error, "Shortcut unavailable", error.Message); }
        HotkeyCaptureBox.Text = _session.State.Settings.Shortcut.DisplayText;
    }

    private async Task RunCommandAsync(Func<Task> command)
    {
        try { await command(); }
        catch (Exception error) { _session.ReportUiError(error); }
    }
}
