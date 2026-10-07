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
        const string typingHelp = "Click to record a new shortcut. Esc cancels. Live models type recognition updates as they arrive. Held Ctrl, Alt, Shift or Win keys delay typing until released. Some editors cannot distinguish fields within the same window. Hold to talk finishes when you release the shortcut; otherwise press it again to finish. Check the destination before dictating, because Cancel cannot undo inserted text.";
        ToolTipService.SetToolTip(HotkeyCaptureBox, typingHelp);
        AutomationProperties.SetHelpText(HotkeyCaptureBox, typingHelp);
        // TextBox handles pointer presses itself, so listen for handled presses to restart capture on click.
        HotkeyCaptureBox.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => StartShortcutCapture()), true);
        _rendering = true;
        HotkeyCaptureBox.Text = _state.Settings.Shortcut.DisplayText;
        PushToTalkChoice.SelectedIndex = _state.Settings.PushToTalk ? 0 : 1;
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
            ToolTipService.SetToolTip(UsageRow, $"Stored on this device. Never audio or text. Session dates, model, word counts, and timing. File: {_session.UsagePath}");
            AutomationProperties.SetHelpText(UsageToggle, _session.UsagePath);
            if (!_recordingShortcut) { HotkeyCaptureBox.Text = _state.Settings.Shortcut.DisplayText; }
            HotkeyCaptureBox.IsEnabled = idle;
            ResetShortcutButton.Visibility = _state.Settings.Shortcut != DictationShortcut.Default ? Visibility.Visible : Visibility.Collapsed;
            ResetShortcutButton.IsEnabled = idle;
            PushToTalkChoice.IsEnabled = idle;
            PushToTalkChoice.SelectedIndex = _state.Settings.PushToTalk ? 0 : 1;
        }
        finally { _rendering = false; }
    }

    // Shared by Render and DictionaryChanged. DictionaryChanged must not call Render, because Render
    // rewrites DictionaryBox.Text whenever the draft is clean and would move the caret while typing.
    private void RenderDictionaryStatus()
    {
        SaveDictionaryButton.IsEnabled = _state.IsIdle && _dictionaryDirty;
        if (_dictionaryDirty) { DictionaryStatusText.Text = "Unsaved changes"; return; }
        // The saved dictionary is normalized to unique, non-empty entries joined by line feeds.
        var entries = _state.Settings.CustomDictionary.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var count = entries switch { 0 => "No entries", 1 => "1 entry", _ => $"{entries} entries" };
        DictionaryStatusText.Text = _state.SelectedModel is { SupportsCustomDictionary: false } model
            ? $"{count} · Not used by {model.DisplayName ?? model.Id}." : count;
    }

    // Warnings and errors stay until dismissed. Success and information notices are shown once for a few
    // seconds while the session is idle; a notice closed with the close button is not reopened.
    private void RenderNotice()
    {
        var notice = _state.Notice;
        var attention = notice.Kind is NoticeKind.Warning or NoticeKind.Error;
        if (!attention || ReferenceEquals(notice, _dismissedNotice))
        {
            StatusInfo.IsOpen = false;
            StatusInfo.Visibility = Visibility.Collapsed;
            return;
        }
        StatusInfo.Severity = notice.Kind == NoticeKind.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        StatusInfo.Title = notice.Title;
        StatusInfo.Message = notice.Message;
        StatusInfo.IsOpen = true;
        StatusInfo.Visibility = Visibility.Visible;
    }

    private void StatusInfoClosing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton) { _dismissedNotice = _state.Notice; }
        sender.Visibility = Visibility.Collapsed;
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

    private void DictationModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || PushToTalkChoice.SelectedIndex < 0) { return; }
        var pushToTalk = PushToTalkChoice.SelectedIndex == 0;
        // RadioButtons can raise SelectionChanged after Render returns; ignore echoes of the current setting.
        if (pushToTalk == _session.State.Settings.PushToTalk) { return; }
        try { _changeMode(pushToTalk); }
        catch (Exception error) { _session.ReportUiError(error); Render(); }
    }

    private void ResetShortcutClicked(object sender, RoutedEventArgs args)
    {
        try { _changeHotkey(DictationShortcut.Default); }
        catch (Exception error) { _session.Notify(NoticeKind.Error, "Shortcut unavailable", error.Message); }
    }

    private void ShortcutGotFocus(object sender, RoutedEventArgs args) => StartShortcutCapture();

    // Focus, click, or Enter on the shortcut box records a new shortcut.
    private void StartShortcutCapture()
    {
        if (_recordingShortcut || !HotkeyCaptureBox.IsEnabled) { return; }
        try
        {
            _captureShortcut(true);
            _recordingShortcut = true;
            _pendingShortcut = null;
            HotkeyCaptureBox.Text = "Press a shortcut; Esc cancels";
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    private void ShortcutKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_recordingShortcut)
        {
            if (args.Key == VirtualKey.Enter) { args.Handled = true; StartShortcutCapture(); }
            return;
        }
        // Tab is never a valid shortcut. Let it move focus; LostFocus ends the capture.
        if (args.Key == VirtualKey.Tab) { return; }
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
