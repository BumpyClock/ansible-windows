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
    private UiSessionObserver? _observer;
    private SessionSnapshot _state;
    private bool _rendering;

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
        const string typingHelp = "Click to record a new shortcut. Esc cancels. The completed transcript is inserted once after recognition succeeds. Held Ctrl, Alt, Shift or Win keys delay insertion until released. Some editors cannot distinguish fields within the same window. Hold to talk finishes when you release the shortcut; otherwise press it again to finish. Cancelling recognition inserts no text. Check the destination before dictating.";
        ToolTipService.SetToolTip(HotkeyCaptureBox, typingHelp);
        AutomationProperties.SetHelpText(HotkeyCaptureBox, typingHelp);
        // TextBox handles pointer presses itself, so listen for handled presses to restart capture on click.
        HotkeyCaptureBox.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => StartShortcutCapture()), true);
        _rendering = true;
        SetShortcutDisplay(_state.Settings.Shortcut.DisplayText);
        PushToTalkChoice.SelectedIndex = _state.Settings.PushToTalk ? 0 : 1;
        RenderInsertion(_state.IsIdle);
        _rendering = false;
        Loaded += (_, _) =>
        {
            _observer = new UiSessionObserver(session, DispatcherQueue, state => { _state = state; Render(); });
        };
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
            BackendChoice.IsEnabled = idle;
            BackendChoice.SelectedIndex = _state.Settings.Backend switch
            {
                NativeBackend.Auto => 0,
                NativeBackend.Cpu => 1,
                NativeBackend.Vulkan => 2,
                _ => throw new InvalidDataException("The recognition processor setting is invalid.")
            };
            BackendRow.Description = _state.Settings.Backend == NativeBackend.Auto
                ? string.IsNullOrEmpty(_state.DeviceSelectionSummary)
                    ? "Auto compares CPU and GPU using a bundled speech sample, then remembers the faster working device for each model. The first preparation takes longer. Audio stays local."
                    : $"Last device comparison: {_state.DeviceSelectionSummary}"
                : _state.Settings.Backend == NativeBackend.Vulkan && _state.SelectedModel?.Family == "nemotron_asr"
                    ? "Nemotron GPU streaming has a known native crash in this build. Choose Auto or CPU for dictation and replay."
                    : "Manual selection applies to the next recognition. Choose Auto to compare devices and recover from a failed GPU qualification.";
            DeviceComparison.Comparison = _state.DeviceComparison;
            DeviceComparison.Summary = _state.DeviceSelectionSummary;
            if (LanguageBox.FocusState == FocusState.Unfocused) { LanguageBox.Text = _state.Language; }
            RenderNotice();
            UsageToggle.IsOn = _state.Settings.CollectUsage;
            HideFromCaptureChoice.IsChecked = _state.Settings.HideFromCapture;
            AutoDetectMeetingsChoice.IsChecked = _state.Settings.AutoDetectMeetings;
            UsageToggle.IsEnabled = idle;
            ToolTipService.SetToolTip(UsageRow, $"Stored on this device. Never audio or text. Session dates, model, word counts, and timing. File: {_session.UsagePath}");
            AutomationProperties.SetHelpText(UsageToggle, _session.UsagePath);
            if (!_recordingShortcut) { SetShortcutDisplay(_state.Settings.Shortcut.DisplayText); }
            HotkeyCaptureBox.IsEnabled = idle;
            ResetShortcutButton.Visibility = _state.Settings.Shortcut != DictationShortcut.Default ? Visibility.Visible : Visibility.Collapsed;
            ResetShortcutButton.IsEnabled = idle;
            PushToTalkChoice.IsEnabled = idle;
            PushToTalkChoice.SelectedIndex = _state.Settings.PushToTalk ? 0 : 1;
            RenderInsertion(idle);
            MicrophoneBoostBox.IsEnabled = idle;
            MicrophoneBoostBox.Value = _state.Settings.MicrophoneBoostDecibels;
        }
        finally { _rendering = false; }
    }

    private void RenderInsertion(bool idle)
    {
        var typing = _state.Settings.InsertionMethod == TextInsertionMethod.Type;
        InsertionChoice.SelectedIndex = typing ? 1 : 0;
        InsertionChoice.IsEnabled = idle;
        InsertionRow.Description = typing
            ? "Types one character at a time. Use this for apps that block paste."
            : "Replaces your clipboard with dictated text and sends the selected paste shortcut. The text stays available for apps that paste later. Requests exclusion from clipboard history and cloud sync. Use Type characters to preserve your clipboard.";
        PasteShortcutRow.Visibility = typing ? Visibility.Collapsed : Visibility.Visible;
        PasteShortcutChoice.IsEnabled = idle;
        PasteShortcutChoice.SelectedIndex = (int)_state.Settings.PasteShortcut;
        TypingGapRow.Visibility = typing ? Visibility.Visible : Visibility.Collapsed;
        TypingGapBox.IsEnabled = idle;
        TypingGapBox.Value = _state.Settings.TypingGapMilliseconds;
    }

    private void RenderNotice()
    {
        var notice = _state.Notice;
        if (!notice.ShouldDisplayOn(NoticeArea.Settings))
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
        if (args.Reason == InfoBarCloseReason.CloseButton) { _session.DismissNotice(_state.Notice); }
        sender.Visibility = Visibility.Collapsed;
    }

    private void BackendChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || BackendChoice is null || BackendChoice.SelectedIndex < 0) { return; }
        try
        {
            _session.SetBackend(BackendChoice.SelectedIndex switch
            {
                0 => NativeBackend.Auto,
                1 => NativeBackend.Cpu,
                2 => NativeBackend.Vulkan,
                _ => throw new InvalidOperationException("Choose a recognition processor.")
            });
        }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); }
    }

    private void LanguageChanged(object sender, TextChangedEventArgs args)
    {
        if (_rendering || LanguageBox is null) { return; }
        try { _session.SetLanguage(LanguageBox.Text); }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); }
    }
    private void UsageToggled(object sender, RoutedEventArgs args)
    {
        if (_rendering) { return; }
        try { _session.SetUsageEnabled(UsageToggle.IsOn); }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); Render(); }
    }

    private void HideFromCaptureChanged(object sender, RoutedEventArgs args)
    {
        if (_rendering || HideFromCaptureChoice.IsChecked is not bool enabled || enabled == _session.State.Settings.HideFromCapture) return;
        try { _session.SetHideFromCapture(enabled); }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); Render(); }
    }

    private void AutoDetectMeetingsChanged(object sender, RoutedEventArgs args)
    {
        if (_rendering || AutoDetectMeetingsChoice.IsChecked is not bool enabled || enabled == _session.State.Settings.AutoDetectMeetings) return;
        try { _session.SetAutoDetectMeetings(enabled); }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); Render(); }
    }

    private void DictationModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || PushToTalkChoice.SelectedIndex < 0) { return; }
        var pushToTalk = PushToTalkChoice.SelectedIndex == 0;
        // Selection controls can raise SelectionChanged after Render returns; ignore echoes of the current setting.
        if (pushToTalk == _session.State.Settings.PushToTalk) { return; }
        try { _changeMode(pushToTalk); }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); Render(); }
    }

    private void InsertionMethodChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || InsertionChoice.SelectedIndex < 0) { return; }
        var method = InsertionChoice.SelectedIndex == 0 ? TextInsertionMethod.Paste : TextInsertionMethod.Type;
        if (method == _session.State.Settings.InsertionMethod) { return; }
        try { _session.SetInsertionMethod(method); }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); Render(); }
    }

    private void TypingGapChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_rendering) { return; }
        if (double.IsNaN(args.NewValue)) { Render(); return; }
        var gap = (int)Math.Round(Math.Clamp(args.NewValue, 0, AppSettings.MaxTypingGapMilliseconds));
        if (gap == _session.State.Settings.TypingGapMilliseconds) { return; }
        try { _session.SetTypingGap(gap); }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); Render(); }
    }

    private void PasteShortcutChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || PasteShortcutChoice.SelectedIndex < 0) { return; }
        var shortcut = (PasteShortcut)PasteShortcutChoice.SelectedIndex;
        if (shortcut == _session.State.Settings.PasteShortcut) { return; }
        try { _session.SetPasteShortcut(shortcut); }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); Render(); }
    }

    private void MicrophoneBoostChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_rendering) { return; }
        if (double.IsNaN(args.NewValue)) { Render(); return; }
        var boost = (int)Math.Round(Math.Clamp(args.NewValue, 0, MicrophoneBoost.MaxDecibels));
        if (boost == _session.State.Settings.MicrophoneBoostDecibels) { Render(); return; }
        try { _session.SetMicrophoneBoost(boost); }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); Render(); }
    }

    private void ResetShortcutClicked(object sender, RoutedEventArgs args)
    {
        try { _changeHotkey(DictationShortcut.Default); }
        catch (Exception error) { _session.Notify(NoticeKind.Error, "Shortcut unavailable", error.Message, NoticeArea.Settings); }
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
            SetShortcutDisplay("Press a shortcut; Esc cancels");
        }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Settings); }
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
            SetShortcutDisplay("Use F1-F11, or Ctrl/Alt/Win with a letter or digit");
            return;
        }
        _pendingShortcut = shortcut;
        SetShortcutDisplay($"{shortcut.DisplayText} (release to apply)");
    }

    private void ShortcutKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (!_recordingShortcut || _pendingShortcut is not { } shortcut || (uint)args.Key != shortcut.Key) { return; }
        args.Handled = true;
        try { _changeHotkey(shortcut); }
        catch (Exception error) { _session.Notify(NoticeKind.Error, "Shortcut unavailable", error.Message, NoticeArea.Settings); }
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
        catch (Exception error) { _session.Notify(NoticeKind.Error, "Shortcut unavailable", error.Message, NoticeArea.Settings); }
        SetShortcutDisplay(_session.State.Settings.Shortcut.DisplayText);
    }

    private void SetShortcutDisplay(string text)
    {
        HotkeyCaptureBox.Text = text;
        if (_recordingShortcut)
        {
            HotkeyKeycaps.Visibility = Visibility.Collapsed;
            HotkeyCaptureStatus.Visibility = Visibility.Visible;
            HotkeyCaptureStatus.Text = text;
            return;
        }

        HotkeyCaptureStatus.Visibility = Visibility.Collapsed;
        HotkeyKeycaps.Visibility = Visibility.Visible;
        var parts = text.Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasModifier = parts.Length > 1;
        HotkeyModifierKeycap.Visibility = hasModifier ? Visibility.Visible : Visibility.Collapsed;
        HotkeyPlusText.Visibility = hasModifier ? Visibility.Visible : Visibility.Collapsed;
        HotkeyModifierText.Text = hasModifier ? string.Join(" + ", parts[..^1]) : string.Empty;
        HotkeyPrimaryText.Text = parts.Length == 0 ? text : parts[^1];
    }
}
