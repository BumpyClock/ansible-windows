using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace Ansible;

public sealed partial class MainPage : Page
{

    private readonly DictationSession _session;
    private readonly Action<DictationShortcut> _changeHotkey;
    private readonly Action<bool> _changeMode;
    private readonly Action<bool> _captureShortcut;
    private bool _recordingShortcut;
    private DictationShortcut? _pendingShortcut;
    private SessionNotice? _dismissedNotice;
    private UiSessionObserver? _observer;
    private SessionSnapshot _state;
    private bool _rendering;
    private bool _dictionaryDirty;

    internal MainPage(DictationSession session, AppPaths paths, Func<nint> windowHandle,
        Action<DictationShortcut> changeHotkey, Action<bool> changeMode, Action<bool> captureShortcut)
    {
        _rendering = true;
        InitializeComponent();
        _session = session;
        _changeHotkey = changeHotkey;
        _changeMode = changeMode;
        _captureShortcut = captureShortcut;
        _state = session.State;
        const string typingHelp = "Live models type recognition updates as they arrive. Held Ctrl, Alt, Shift or Win keys delay typing until released. Some editors cannot distinguish fields within the same window. Hold to talk finishes when you release the shortcut; otherwise press it again to finish. Check the destination before dictating, because Cancel cannot undo inserted text.";
        ToolTipService.SetToolTip(HotkeyCaptureBox, typingHelp);
        AutomationProperties.SetHelpText(HotkeyCaptureBox, typingHelp);
        _rendering = true;
        HotkeyCaptureBox.Text = _state.Settings.Shortcut.DisplayText;
        PushToTalkToggle.IsOn = _state.Settings.PushToTalk;
        _rendering = false;
        Loaded += (_, _) =>
            _observer = new UiSessionObserver(session, DispatcherQueue, state => { _state = state; Render(); });
        Unloaded += (_, _) =>
        {
            EndShortcutCapture();
            _observer?.Dispose(); _observer = null;
        };
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            var idle = _state.IsIdle;
            LanguageBox.IsEnabled = _state.Phase == DictationPhase.Ready;
            if (LanguageBox.FocusState == FocusState.Unfocused) { LanguageBox.Text = _state.Language; }
            if (!_dictionaryDirty) { DictionaryBox.Text = _state.Settings.CustomDictionary; }
            DictionaryBox.IsEnabled = idle;
            RenderDictionaryStatus();
            RenderNotice();
            UsageToggle.IsOn = _state.Settings.CollectUsage;
            UsageToggle.IsEnabled = idle;
            ToolTipService.SetToolTip(UsageRow, _session.UsagePath);
            AutomationProperties.SetHelpText(UsageToggle, _session.UsagePath);
            if (!_recordingShortcut) { HotkeyCaptureBox.Text = _state.Settings.Shortcut.DisplayText; }
            ChangeShortcutButton.IsEnabled = idle;
            PushToTalkToggle.IsEnabled = idle;
            PushToTalkToggle.IsOn = _state.Settings.PushToTalk;
        }
        finally { _rendering = false; }
    }

    // Shared by Render and DictionaryChanged. DictionaryChanged must not call Render, because Render
    // rewrites DictionaryBox.Text whenever the draft is clean and would move the caret while typing.
    private void RenderDictionaryStatus()
    {
        SaveDictionaryButton.IsEnabled = _state.IsIdle && _dictionaryDirty;
        DictionaryStatusText.Text = _dictionaryDirty ? "Unsaved changes"
            : _state.SelectedModel is not { } selected
                ? "Choose a model to see whether dictionary hints are supported. Your list stays on this device."
                : selected.SupportsCustomDictionary
                    ? selected.Mode == "offline"
                        ? $"{selected.DisplayName ?? selected.Id} uses the saved dictionary as context for WAV transcription."
                        : $"{selected.DisplayName ?? selected.Id} uses the saved dictionary as context for file and streaming recognition."
                    : $"{selected.DisplayName ?? selected.Id} does not support dictionary hints. Your list is kept for supported models.";
    }

    // Warnings and errors stay until dismissed. Success and information notices are shown once for a few
    // seconds while the session is idle; a notice closed with the close button is not reopened.
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
        RenderDictionaryStatus();
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
}
