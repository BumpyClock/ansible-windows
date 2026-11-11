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
    private readonly Action<DictationShortcut> _changeRecordHotkey;
    private readonly Action<DictationShortcut> _changeRecordPasteHotkey;
    private readonly Action<bool> _enableRecordChord;
    private readonly CaptureStore _captures;
    private readonly Action<bool> _changeMode;
    private readonly Action<bool> _captureShortcut;
    private readonly LocalDiagnosticLog _diagnostics;
    private readonly DispatcherTimer _diagnosticStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _recordingShortcut;
    private ShortcutField? _captureField;
    private Task? _captureSizeTask;
    private bool _capturesSaved;
    private Task? _captureDeleteTask;
    private bool _wasRecordTake;
    private bool _joining;
    private DictationShortcut? _pendingShortcut;
    private bool _shortcutPrimarySeen;
    private UiSessionObserver? _observer;
    private SessionSnapshot _state;
    private bool _rendering;
    private MeetingCleanupSettings? _renderedCleanupSettings;

    private const string ErrorNoticeKey = "settings-error";
    private const string BenchmarkNoticeKey = "device-benchmark";
    private readonly AppNotices _notices;

    // Stored values are the providers' identifiers; the list shows sentence-case labels in the same order.
    private static readonly (string Value, string Label)[] ReasoningEfforts =
    [
        ("none", "None"), ("minimal", "Minimal"), ("low", "Low"), ("medium", "Medium"),
        ("high", "High"), ("xhigh", "Extra high"), ("max", "Maximum")
    ];

    // Catalog entries by ID and by full display name, so model names here match the Speech models page.
    private readonly Dictionary<string, NativeModelEntry> _catalogEntries = new(StringComparer.Ordinal);

    internal event Action? ShortcutLessonRequested;

    internal MainPage(DictationSession session, AppPaths paths, Func<nint> windowHandle,
        Action<DictationShortcut> changeHotkey, Action<bool> changeMode, Action<bool> captureShortcut,
        LocalDiagnosticLog diagnostics, AppNotices notices, Action<DictationShortcut> changeRecordHotkey,
        Action<bool> enableRecordChord, CaptureStore captures, Action<DictationShortcut> changeRecordPasteHotkey)
    {
        _rendering = true;
        InitializeComponent();
        _session = session;
        _notices = notices;
        _changeHotkey = changeHotkey;
        _changeRecordHotkey = changeRecordHotkey;
        _changeRecordPasteHotkey = changeRecordPasteHotkey;
        _enableRecordChord = enableRecordChord;
        _captures = captures;
        _changeMode = changeMode;
        _captureShortcut = captureShortcut;
        _diagnostics = diagnostics;
        _state = session.State;
        foreach (var effort in ReasoningEfforts)
        {
            CopilotCleanupEffortChoice.Items.Add(effort.Label);
            OpenAICleanupEffortChoice.Items.Add(effort.Label);
        }
        var copilotDefault = new MeetingCleanupSettings().CopilotModel;
        CopilotCleanupModelBox.PlaceholderText = copilotDefault;
        // The model cannot be empty; validation rejects it, so the description names the default instead.
        CopilotModelRow.Description = $"The GitHub Copilot model to use. The default is {copilotDefault}.";
        // A read-only file load that owns no resources, so shutdown does not join it.
        _ = LoadCatalogNamesAsync(paths.ModelCatalog);
        const string typingHelp = "Click to record a new shortcut. Esc cancels. The completed transcript is inserted once after recognition succeeds. Held Ctrl, Alt, Shift or Win keys delay insertion until released. Some editors cannot distinguish fields within the same window. Hold to talk finishes when you release the shortcut; otherwise press it again to finish. Cancelling recognition inserts no text. Check the destination before dictating.";
        ToolTipService.SetToolTip(HotkeyField.CaptureBox, typingHelp);
        AutomationProperties.SetHelpText(HotkeyField.CaptureBox, typingHelp);
        AutomationProperties.SetAutomationId(HotkeyField.CaptureBox, "HotkeyCaptureBox");
        AutomationProperties.SetName(HotkeyField.CaptureBox, "Shortcut");
        AutomationProperties.SetName(RecordHotkeyField.CaptureBox, "Record shortcut");
        AutomationProperties.SetName(RecordPasteHotkeyField.CaptureBox, "Paste capture shortcut");
        WireShortcutField(HotkeyField);
        WireShortcutField(RecordHotkeyField);
        WireShortcutField(RecordPasteHotkeyField);
        _rendering = true;
        ShowShortcuts();
        PushToTalkChoice.SelectedIndex = _state.Settings.PushToTalk ? 0 : 1;
        RenderInsertion(_state.IsIdle);
        _rendering = false;
        Loaded += (_, _) =>
        {
            _observer = new UiSessionObserver(session, DispatcherQueue, state => { _state = state; Render(); });
            if (!_joining) _captureSizeTask = RefreshCaptureSizeAsync();
            if (_state.Settings.LocalDiagnostics) _diagnosticStatusTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _diagnosticStatusTimer.Stop();
            EndShortcutCapture();
            _observer?.Dispose(); _observer = null;
        };
        _diagnosticStatusTimer.Tick += (_, _) => RenderDiagnosticStatus();
    }

    // A failed change replaces the page error. The next change that succeeds clears it.
    private bool Try(Action change, string title = "Operation failed")
    {
        try { change(); }
        catch (Exception error) { ReportError(error, title); return false; }
        ClearError();
        return true;
    }

    private void ReportError(Exception error, string title = "Operation failed") =>
        _notices.Set(ErrorNoticeKey, new NoticeContent(NoticeSeverity.Error, title, error.Message), NoticeArea.Settings);

    private void ClearError() => _notices.Set(ErrorNoticeKey, null);

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
            RenderBenchmark();
            if (LanguageBox.FocusState == FocusState.Unfocused) { LanguageBox.Text = _state.Language; }
            UsageToggle.IsOn = _state.Settings.CollectUsage;
            DiagnosticsToggle.IsOn = _state.Settings.LocalDiagnostics;
            DiagnosticsToggle.IsEnabled = idle;
            if (_state.Settings.LocalDiagnostics) _diagnosticStatusTimer.Start();
            else _diagnosticStatusTimer.Stop();
            RenderDiagnosticStatus();
            HideFromCaptureToggle.IsOn = _state.Settings.HideFromCapture;
            AutoDetectMeetingsToggle.IsOn = _state.Settings.AutoDetectMeetings;
            UsageToggle.IsEnabled = idle;
            ToolTipService.SetToolTip(UsageToggle,$"Stored on this device. Never audio or text. Session dates, model, word counts, and timing. File: {_session.UsagePath}");
            AutomationProperties.SetHelpText(UsageToggle, _session.UsagePath);
            if (!_recordingShortcut) { ShowShortcuts(); }
            HotkeyField.IsEnabled = idle;
            ResetShortcutButton.Visibility = _state.Settings.Shortcut != DictationShortcut.Default ? Visibility.Visible : Visibility.Collapsed;
            ResetShortcutButton.IsEnabled = idle;
            ChangeShortcutButton.IsEnabled = idle;
            RecordChordToggle.IsOn = _state.Settings.RecordChordEnabled;
            RecordChordToggle.IsEnabled = idle;
            RecordToggleModeToggle.IsOn = _state.Settings.RecordToggleMode;
            RecordToggleModeToggle.IsEnabled = idle;
            IncludeVideoLinkToggle.IsOn = _state.Settings.IncludeVideoLink;
            IncludeVideoLinkToggle.IsEnabled = idle;
            RecordHotkeyField.IsEnabled = idle;
            ChangeRecordShortcutButton.IsEnabled = idle;
            ResetRecordShortcutButton.IsEnabled = idle;
            ResetRecordShortcutButton.Visibility = _state.Settings.RecordShortcut != DictationShortcut.RecordDefault ? Visibility.Visible : Visibility.Collapsed;
            RecordPasteHotkeyField.IsEnabled = idle;
            ChangeRecordPasteShortcutButton.IsEnabled = idle;
            ResetRecordPasteShortcutButton.IsEnabled = idle;
            ResetRecordPasteShortcutButton.Visibility = _state.Settings.RecordPasteShortcut != DictationShortcut.RecordPasteDefault ? Visibility.Visible : Visibility.Collapsed;
            DeleteCapturesButton.IsEnabled = _capturesSaved && !_joining && idle && _captureSizeTask is not { IsCompleted: false } && _captureDeleteTask is not { IsCompleted: false };
            if (!_joining && _wasRecordTake && !_state.IsRecordTake && _captureSizeTask is not { IsCompleted: false })
                _captureSizeTask = RefreshCaptureSizeAsync();
            _wasRecordTake = _state.IsRecordTake;
            PushToTalkChoice.IsEnabled = idle;
            PushToTalkChoice.SelectedIndex = _state.Settings.PushToTalk ? 0 : 1;
            OverlayFollowsCaretToggle.IsOn = _state.Settings.OverlayFollowsCaret;
            OverlayFollowsCaretToggle.IsEnabled = idle;
            RenderInsertion(idle);
            MicrophoneBoostBox.IsEnabled = idle;
            MicrophoneBoostBox.Value = _state.Settings.MicrophoneBoostDecibels;
            CleanupSettingsPanel.IsEnabled = idle;
            RenderCleanupSettings();
        }
        finally { _rendering = false; }
    }

    // The device result describes the last model load and changes only when recognition prepares again, so the
    // processor choice never reflows it.
    private void RenderBenchmark()
    {
        // Activity stays set after the terminal snapshot; only these phases mean the benchmark still owns the session.
        var running = _state.Activity == SessionActivity.DeviceBenchmark &&
            _state.Phase is DictationPhase.Benchmarking or DictationPhase.Cancelling;
        var model = _state.SelectedModel;
        var fullName = model is null ? null : model.DisplayName ?? model.Id;
        var plainName = fullName is null ? null : PlainModelName(fullName);
        // Plain words for the user: the graphics card is the Vulkan backend, and the test uses the packaged sample.
        var status = running
            ? _state.Phase == DictationPhase.Cancelling ? "Stopping the speed test."
                : _state.BenchmarkingBackend switch
                {
                    NativeBackend.Cpu => "Testing the CPU with the sample recording included with Ansible.",
                    NativeBackend.Vulkan => "Testing the graphics card with the sample recording included with Ansible.",
                    _ => "Preparing the speed test."
                }
            : model is null ? "Install a dictation model to compare the CPU and graphics card."
            : _state.CanStart ? $"Tests how fast the CPU and graphics card run {plainName}."
            : "You can run a speed test when Ansible is not busy.";
        BenchmarkRow.Description = status;
        BenchmarkProgress.IsActive = running;
        BenchmarkProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        BenchmarkButtonText.Text = running ? "Cancel" : "Run speed test";
        BenchmarkButton.IsEnabled = running ? _state.IsBenchmarking : _state.CanStart;
        AutomationProperties.SetName(BenchmarkButton, running ? "Cancel speed test" : "Run speed test");
        // The row names the model plainly; the full catalog name, precision included, is one hover away.
        ToolTipService.SetToolTip(BenchmarkButton, plainName == fullName ? null : fullName);
        var comparison = _state.DeviceComparison;
        DeviceComparison.Show(comparison, _state.DeviceSelectionSummary,
            comparison is null ? "" : PlainModelName(comparison.ModelName), _state.Settings.Backend);
    }

    // Installed models and device comparisons carry the full catalog name (or the ID when it has none). Until the
    // catalog loads, or if it cannot, that name is shown as is.
    private string PlainModelName(string nameOrId) =>
        _catalogEntries.TryGetValue(nameOrId, out var entry) ? ModelCardText.PlainName(entry) : nameOrId;

    private async Task LoadCatalogNamesAsync(string catalogPath)
    {
        IReadOnlyList<NativeModelEntry> entries;
        try { entries = await NativeModelCatalog.LoadAsync(catalogPath); }
        catch (Exception) { return; } // The Speech models page reports an unreadable catalog; full names stay here.
        foreach (var entry in entries)
        {
            _catalogEntries.TryAdd(entry.Id, entry);
            if (!string.IsNullOrWhiteSpace(entry.DisplayName)) _catalogEntries.TryAdd(entry.DisplayName, entry);
        }
        RenderBenchmark();
    }

    private async void BenchmarkClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_state.IsBenchmarking) { await _session.CancelAsync(); return; }
            _notices.Set(BenchmarkNoticeKey, null);
            // Failures and timeouts arrive as a Settings notice from the terminal snapshot.
            await _session.BenchmarkDevicesAsync();
        }
        catch (Exception error)
        {
            _notices.Set(BenchmarkNoticeKey, new NoticeContent(NoticeSeverity.Error, "Speed test failed", error.Message),
                NoticeArea.Settings);
        }
    }

    private void RenderInsertion(bool idle)
    {
        var typing = _state.Settings.InsertionMethod == TextInsertionMethod.Type;
        InsertionChoice.SelectedIndex = typing ? 1 : 0;
        InsertionChoice.IsEnabled = idle;
        InsertionRow.Description = typing
            ? "Types each character at a steady pace and leaves your clipboard alone. Slower than paste."
            : "Copies the text and pastes it. Windows is asked to keep it out of clipboard history and sync.";
        PasteShortcutRow.Visibility = typing ? Visibility.Collapsed : Visibility.Visible;
        PasteShortcutChoice.IsEnabled = idle;
        PasteShortcutChoice.SelectedIndex = (int)_state.Settings.PasteShortcut;
        TypingGapRow.Visibility = typing ? Visibility.Visible : Visibility.Collapsed;
        TypingGapBox.IsEnabled = idle;
        TypingGapBox.Value = _state.Settings.TypingGapMilliseconds;
    }

    private void RenderCleanupSettings()
    {
        var settings = _state.Settings.MeetingCleanup;
        if (settings == _renderedCleanupSettings) return;
        _renderedCleanupSettings = settings;
        CleanupProviderChoice.SelectedIndex = (int)settings.Provider;
        CopilotCleanupModelBox.Text = settings.CopilotModel;
        CopilotCleanupEffortChoice.SelectedIndex = Array.FindIndex(ReasoningEfforts, effort => effort.Value == settings.CopilotReasoningEffort);
        OpenAICleanupModelBox.Text = settings.OpenAIModel;
        OpenAICleanupEffortChoice.SelectedIndex = Array.FindIndex(ReasoningEfforts, effort => effort.Value == settings.OpenAIReasoningEffort);
        AutomaticCleanupToggle.IsOn = settings.Automatic;
        RenderCleanupProvider();
        RenderKeyStatus();
    }

    private void CleanupProviderChanged(object sender, SelectionChangedEventArgs args)
    {
        // OpenAIEffortRow is the last element RenderCleanupProvider touches, so it exists once the page is built.
        if (OpenAIEffortRow is null) return;
        RenderCleanupProvider();
        if (_rendering) return;
        // Consent names one provider, so a new provider turns automatic cleanup off until the user confirms it.
        var wasAutomatic = _session.State.Settings.MeetingCleanup.Automatic;
        if (!SaveCleanupSettings(automatic: false) || !wasAutomatic) return;
        AutomaticCleanupToggle.IsOn = false;
        _notices.Confirm("Automatic cleanup is off. Turn it on again to send meetings to the new service.");
    }

    private void CleanupAdvancedClicked(object sender, RoutedEventArgs args)
    {
        var open = CleanupAdvancedFields.Visibility != Visibility.Visible;
        CleanupAdvancedFields.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CleanupAdvancedButton.Content = open ? "Hide" : "Show";
        AutomationProperties.SetName(CleanupAdvancedButton,
            open ? "Hide advanced cleanup settings" : "Show advanced cleanup settings");
    }

    private void CleanupEffortChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_rendering) SaveCleanupSettings();
    }

    private void CleanupModelCommitted(object sender, RoutedEventArgs args)
    {
        if (!_rendering) SaveCleanupSettings();
    }

    private void CleanupModelKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        SaveCleanupSettings();
    }

    private void RenderCleanupProvider()
    {
        // GitHub Copilot has a default model, so its model and effort both sit under Advanced. OpenAI needs a model,
        // which stays in view with the key; only its effort is advanced.
        var copilot = CleanupProviderChoice.SelectedIndex == 0;
        var copilotVisibility = copilot ? Visibility.Visible : Visibility.Collapsed;
        var openAIVisibility = copilot ? Visibility.Collapsed : Visibility.Visible;
        CopilotModelRow.Visibility = copilotVisibility;
        CopilotEffortRow.Visibility = copilotVisibility;
        OpenAICleanupFields.Visibility = openAIVisibility;
        OpenAIEffortRow.Visibility = openAIVisibility;
        CleanupAdvancedRow.Description = copilot ? "Model and thinking effort." : "Thinking effort.";
        // Copilot cleanup runs the GitHub Copilot command-line tool, which needs a signed-in GitHub account.
        CleanupProviderRow.Description = copilot
            ? "Uses the GitHub Copilot command-line tool installed on this PC. Sign in to it with your GitHub account first. Never switches to another service or account."
            : "Sends text to OpenAI with your API key, billed separately from ChatGPT. The model must support structured outputs. Never switches to another service or account.";
    }

    private void RenderKeyStatus()
    {
        try
        {
            OpenAIKeyRow.Description = MeetingCleanupService.HasKey
                ? "API key saved in Windows Credential Locker. It is not stored in settings or logs."
                : "No key saved yet. Keys are kept in Windows Credential Locker, never in settings or logs.";
        }
        catch (Exception) { OpenAIKeyRow.Description ="Windows Credential Locker could not be read. Save the key again or check Windows credential access."; }
    }

    private void SaveOpenAIKeyClicked(object sender, RoutedEventArgs args)
    {
        if (!Try(() => MeetingCleanupService.SaveKey(OpenAIKeyBox.Password.Trim()))) return;
        OpenAIKeyBox.Password = "";
        RenderKeyStatus();
    }

    private void RemoveOpenAIKeyClicked(object sender, RoutedEventArgs args)
    {
        if (!Try(MeetingCleanupService.RemoveKey)) return;
        OpenAIKeyBox.Password = "";
        RenderKeyStatus();
    }

    // Cleanup fields save as they change. The saved opt-in turns on only through the confirmed toggle.
    private bool SaveCleanupSettings(bool? automatic = null)
    {
        try
        {
            var saved = _session.State.Settings.MeetingCleanup;
            var settings = new MeetingCleanupSettings
            {
                Provider = (MeetingCleanupProvider)CleanupProviderChoice.SelectedIndex,
                Automatic = automatic ?? saved.Automatic,
                CopilotModel = CopilotCleanupModelBox.Text.Trim(),
                CopilotReasoningEffort = SelectedEffort(CopilotCleanupEffortChoice),
                OpenAIModel = OpenAICleanupModelBox.Text.Trim(),
                OpenAIReasoningEffort = SelectedEffort(OpenAICleanupEffortChoice)
            };
            if (settings == saved) return true;
            settings.Validate();
            if (settings.Automatic && settings.Provider == MeetingCleanupProvider.OpenAI && !MeetingCleanupService.HasKey)
                throw new InvalidOperationException("Save an OpenAI API key before enabling automatic cleanup.");
            _session.SetMeetingCleanup(settings);
            ClearError();
            return true;
        }
        catch (Exception error)
        {
            ReportError(error);
            // Show the saved values again so the fields never claim settings that were rejected.
            _renderedCleanupSettings = null;
            Render();
            return false;
        }
    }

    // No selection maps to an empty value, which validation rejects with its own message.
    private static string SelectedEffort(ComboBox choice) =>
        choice.SelectedIndex >= 0 && choice.SelectedIndex < ReasoningEfforts.Length ? ReasoningEfforts[choice.SelectedIndex].Value : "";

    private async void AutomaticCleanupToggled(object sender, RoutedEventArgs args)
    {
        if (_rendering || AutomaticCleanupToggle.IsOn == _session.State.Settings.MeetingCleanup.Automatic) return;
        if (!AutomaticCleanupToggle.IsOn)
        {
            SaveCleanupSettings(automatic: false);
            return;
        }
        // Enabling sends meeting text without another prompt, so the opt-in is saved only after this confirmation.
        var service = CleanupProviderChoice.SelectedIndex == 0 ? "GitHub Copilot" : "OpenAI";
        var dialog = new ContentDialog
        {
            Title = $"Send each new completed meeting transcript to {service} without asking again?",
            Content = new TextBlock
            {
                Text = "Enabling this authorizes sending each new completed meeting transcript to the selected service without another confirmation. Applies to meetings started after saving. Saved meetings are not uploaded automatically.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Turn on",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
            SaveCleanupSettings(automatic: true) && _session.State.Settings.MeetingCleanup.Automatic)
        {
            _notices.Confirm("Automatic cleanup is on for new meetings.");
            return;
        }
        // Cancelled or not saved: the switch returns to the saved value. The Toggled event this raises finds
        // them equal and returns at the guard above.
        AutomaticCleanupToggle.IsOn = _session.State.Settings.MeetingCleanup.Automatic;
    }

    private void BackendChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || BackendChoice is null || BackendChoice.SelectedIndex < 0) { return; }
        Try(() => _session.SetBackend(BackendChoice.SelectedIndex switch
        {
            0 => NativeBackend.Auto,
            1 => NativeBackend.Cpu,
            2 => NativeBackend.Vulkan,
            _ => throw new InvalidOperationException("Choose a recognition processor.")
        }));
    }

    private void LanguageChanged(object sender, TextChangedEventArgs args)
    {
        if (_rendering || LanguageBox is null) { return; }
        Try(() => _session.SetLanguage(LanguageBox.Text));
    }

    private void UsageToggled(object sender, RoutedEventArgs args)
    {
        if (_rendering) { return; }
        if (!Try(() => _session.SetUsageEnabled(UsageToggle.IsOn))) Render();
    }

    private void DiagnosticsToggled(object sender, RoutedEventArgs args)
    {
        if (_rendering || DiagnosticsToggle.IsOn == _session.State.Settings.LocalDiagnostics) return;
        if (!Try(() => _session.SetLocalDiagnosticsEnabled(DiagnosticsToggle.IsOn))) Render();
    }

    private void RenderDiagnosticStatus()
    {
        DiagnosticsRow.Description = _diagnostics.Error ??
            (_diagnostics.DroppedEvents > 0
                ? $"{_diagnostics.DroppedEvents} diagnostic records were lost in this run."
                : "Keeps a log of app events on this device for troubleshooting. No audio or transcript text is recorded.");
    }

    private void OverlayFollowsCaretToggled(object sender, RoutedEventArgs args)
    {
        if (_rendering || OverlayFollowsCaretToggle.IsOn == _session.State.Settings.OverlayFollowsCaret) { return; }
        if (!Try(() => _session.SetOverlayFollowsCaret(OverlayFollowsCaretToggle.IsOn))) Render();
    }

    private void HideFromCaptureChanged(object sender, RoutedEventArgs args)
    {
        var enabled = HideFromCaptureToggle.IsOn;
        if (_rendering || enabled == _session.State.Settings.HideFromCapture) return;
        if (!Try(() => _session.SetHideFromCapture(enabled))) Render();
    }

    private void AutoDetectMeetingsChanged(object sender, RoutedEventArgs args)
    {
        var enabled = AutoDetectMeetingsToggle.IsOn;
        if (_rendering || enabled == _session.State.Settings.AutoDetectMeetings) return;
        if (!Try(() => _session.SetAutoDetectMeetings(enabled))) Render();
    }

    private void DictationModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || PushToTalkChoice.SelectedIndex < 0) { return; }
        var pushToTalk = PushToTalkChoice.SelectedIndex == 0;
        // Selection controls can raise SelectionChanged after Render returns; ignore echoes of the current setting.
        if (pushToTalk == _session.State.Settings.PushToTalk) { return; }
        if (!Try(() => _changeMode(pushToTalk))) Render();
    }

    private void InsertionMethodChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || InsertionChoice.SelectedIndex < 0) { return; }
        var method = InsertionChoice.SelectedIndex == 0 ? TextInsertionMethod.Paste : TextInsertionMethod.Type;
        if (method == _session.State.Settings.InsertionMethod) { return; }
        if (!Try(() => _session.SetInsertionMethod(method))) Render();
    }

    private void TypingGapChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_rendering) { return; }
        if (double.IsNaN(args.NewValue)) { Render(); return; }
        var gap = (int)Math.Round(Math.Clamp(args.NewValue, 0, AppSettings.MaxTypingGapMilliseconds));
        if (gap == _session.State.Settings.TypingGapMilliseconds) { return; }
        if (!Try(() => _session.SetTypingGap(gap))) Render();
    }

    private void PasteShortcutChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_rendering || PasteShortcutChoice.SelectedIndex < 0) { return; }
        var shortcut = (PasteShortcut)PasteShortcutChoice.SelectedIndex;
        if (shortcut == _session.State.Settings.PasteShortcut) { return; }
        if (!Try(() => _session.SetPasteShortcut(shortcut))) Render();
    }

    private void MicrophoneBoostChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_rendering) { return; }
        if (double.IsNaN(args.NewValue)) { Render(); return; }
        var boost = (int)Math.Round(Math.Clamp(args.NewValue, 0, MicrophoneBoost.MaxDecibels));
        if (boost == _session.State.Settings.MicrophoneBoostDecibels) { Render(); return; }
        if (!Try(() => _session.SetMicrophoneBoost(boost))) Render();
    }

    private void ChangeShortcutClicked(object sender, RoutedEventArgs args) =>
        HotkeyField.CaptureBox.Focus(FocusState.Programmatic);

    private void ChangeRecordShortcutClicked(object sender, RoutedEventArgs args) =>
        RecordHotkeyField.CaptureBox.Focus(FocusState.Programmatic);

    private void ResetRecordShortcutClicked(object sender, RoutedEventArgs args) =>
        Try(() => _changeRecordHotkey(DictationShortcut.RecordDefault), "Record shortcut unavailable");

    private void ChangeRecordPasteShortcutClicked(object sender, RoutedEventArgs args) =>
        RecordPasteHotkeyField.CaptureBox.Focus(FocusState.Programmatic);

    private void ResetRecordPasteShortcutClicked(object sender, RoutedEventArgs args) =>
        Try(() => _changeRecordPasteHotkey(DictationShortcut.RecordPasteDefault), "Paste capture shortcut unavailable");

    private void RecordChordToggled(object sender, RoutedEventArgs args)
    {
        if (_rendering || RecordChordToggle.IsOn == _state.Settings.RecordChordEnabled) return;
        if (!Try(() => _enableRecordChord(RecordChordToggle.IsOn), "Record shortcut unavailable")) Render();
    }

    private void IncludeVideoLinkToggled(object sender, RoutedEventArgs args)
    {
        if (_rendering || IncludeVideoLinkToggle.IsOn == _state.Settings.IncludeVideoLink) return;
        if (!Try(() => _session.SetIncludeVideoLink(IncludeVideoLinkToggle.IsOn))) Render();
    }

    private void RecordToggleModeToggled(object sender, RoutedEventArgs args)
    {
        if (_rendering || RecordToggleModeToggle.IsOn == _state.Settings.RecordToggleMode) return;
        if (!Try(() => _session.SetRecordToggleMode(RecordToggleModeToggle.IsOn))) Render();
    }

    private static string FormatCaptureSize(long bytes)
    {
        if (bytes <= 0) return "No captures saved.";
        var megabytes = bytes / (1024.0 * 1024);
        if (megabytes >= 10) return $"{Math.Round(megabytes):N0} MB on this device.";
        if (megabytes < 0.05) return "Less than 0.1 MB on this device.";
        return $"{megabytes:N1} MB on this device.";
    }

    private async Task RefreshCaptureSizeAsync()
    {
        DeleteCapturesButton.IsEnabled = false;
        try
        {
            await WindowsRecordTakeFactory.RefreshClipboardProtectionAsync();
            var bytes = await _captures.GetSizeAsync();
            _capturesSaved = bytes > 0;
            CaptureStorageRow.Description = $"{FormatCaptureSize(bytes)} Oldest takes are removed after seven days or above 2 GB.";
        }
        catch (Exception error) { ReportError(error, "Capture storage unavailable"); }
        finally { DeleteCapturesButton.IsEnabled = _capturesSaved && !_joining && _state.IsIdle; }
    }

    private async void DeleteCapturesClicked(object sender, RoutedEventArgs args)
    {
        if (_joining || !_state.IsIdle || _captureDeleteTask is { IsCompleted: false }) return;
        _captureDeleteTask = DeleteCapturesAsync();
        await _captureDeleteTask;
    }

    private async Task DeleteCapturesAsync()
    {
        DeleteCapturesButton.IsEnabled = false;
        try
        {
            await WindowsRecordTakeFactory.RefreshClipboardProtectionAsync();
            var result = await _captures.DeleteAllAsync();
            await RefreshCaptureSizeAsync();
            _notices.Set("capture-delete", result.FailedTakes > 0
                ? new NoticeContent(NoticeSeverity.Warning, "Some captures could not be deleted", "Close apps using saved captures and try again. In-use captures were kept; other eligible captures were deleted.")
                : result.ProtectedTakes > 0
                    ? new NoticeContent(NoticeSeverity.Warning, "Some captures are in use", "The current bundle and clipboard pictures were kept. Other saved captures were deleted.")
                    : null, NoticeArea.Settings);
        }
        catch (Exception error) { ReportError(error, "Captures could not be deleted"); }
        finally { DeleteCapturesButton.IsEnabled = _capturesSaved && !_joining && _state.IsIdle; }
    }

    internal async Task JoinAsync()
    {
        _joining = true;
        if (_captureSizeTask is { } task) await task;
        if (_captureDeleteTask is { } deletion) await deletion;
    }

    internal void ResumeAfterFailedShutdown() => _joining = false;

    private void PracticeShortcutClicked(object sender, RoutedEventArgs args) => ShortcutLessonRequested?.Invoke();

    private void ResetShortcutClicked(object sender, RoutedEventArgs args) =>
        Try(() => _changeHotkey(DictationShortcut.Default), "Shortcut unavailable");

    // Focus, click, or Enter on a shortcut field records a new shortcut.
    private void WireShortcutField(ShortcutField field)
    {
        var box = field.CaptureBox;
        box.GotFocus += (_, _) => StartShortcutCapture(field);
        box.KeyDown += (_, args) => ShortcutKeyDown(field, args);
        box.KeyUp += (_, args) => ShortcutKeyUp(args);
        box.LostFocus += (_, _) => EndShortcutCapture();
        // TextBox handles pointer presses itself, so listen for handled presses to restart capture on click.
        box.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => StartShortcutCapture(field)), true);
    }

    private void StartShortcutCapture(ShortcutField field)
    {
        if (_recordingShortcut || !field.IsEnabled) { return; }
        try
        {
            _captureShortcut(true);
            _captureField = field;
            _recordingShortcut = true;
            _pendingShortcut = null;
            _shortcutPrimarySeen = false;
            field.ShowStatus("Press a shortcut; Esc cancels");
        }
        catch (Exception error) { ReportError(error); }
    }

    private void ShortcutKeyDown(ShortcutField field, KeyRoutedEventArgs args)
    {
        if (_captureField is not { } capturing)
        {
            if (args.Key == VirtualKey.Enter) { args.Handled = true; StartShortcutCapture(field); }
            return;
        }
        // Tab is never a valid shortcut. Let it move focus; LostFocus ends the capture.
        if (args.Key == VirtualKey.Tab) { return; }
        args.Handled = true;
        if (args.Key == VirtualKey.Escape) { EndShortcutCapture(); return; }
        uint modifiers = 0;
        if (Down(VirtualKey.Menu)) { modifiers |= 1; }
        if (Down(VirtualKey.Control)) { modifiers |= 2; }
        if (Down(VirtualKey.Shift)) { modifiers |= 4; }
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) { modifiers |= 8; }
        if (IsModifierKey(args.Key))
        {
            if (!_shortcutPrimarySeen)
            {
                var candidate = new DictationShortcut(modifiers, 0);
                _pendingShortcut = candidate.IsModifierOnly ? candidate : null;
                if (_pendingShortcut is { } modifierOnly)
                    capturing.ShowStatus($"{modifierOnly.DisplayText} (release to apply)");
            }
            return;
        }
        _shortcutPrimarySeen = true;
        _pendingShortcut = null;
        var shortcut = new DictationShortcut(modifiers, (uint)args.Key);
        if (!shortcut.IsValid)
        {
            capturing.ShowStatus("Use Ctrl + Win, Alt + Win, F1-F11, or a modified letter or digit");
            return;
        }
        _pendingShortcut = shortcut;
        capturing.ShowStatus($"{shortcut.DisplayText} (release to apply)");
    }

    private void ShortcutKeyUp(KeyRoutedEventArgs args)
    {
        if (_captureField is not { } capturing) { return; }
        if (_pendingShortcut is not { } shortcut)
        {
            // An invalid chord leaves nothing pending. Releasing its primary key must end it, or a later
            // modifier-only shortcut would never be accepted during this capture.
            if (!IsModifierKey(args.Key)) { _shortcutPrimarySeen = false; }
            return;
        }
        if (shortcut.IsModifierOnly)
        {
            if (!new ModifierShortcutState(shortcut.Modifiers).IsChordKey((uint)args.Key)) return;
        }
        else if ((uint)args.Key != shortcut.Key) { return; }
        args.Handled = true;
        Try(() =>
        {
            if (capturing == RecordPasteHotkeyField) _changeRecordPasteHotkey(shortcut);
            else if (capturing == RecordHotkeyField) _changeRecordHotkey(shortcut);
            else _changeHotkey(shortcut);
        }, "Shortcut unavailable");
        EndShortcutCapture();
    }

    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private static bool IsModifierKey(VirtualKey key) =>
        key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private void EndShortcutCapture()
    {
        if (!_recordingShortcut) { return; }
        _recordingShortcut = false;
        _pendingShortcut = null;
        _shortcutPrimarySeen = false;
        try { _captureShortcut(false); }
        catch (Exception error) { ReportError(error, "Shortcut unavailable"); }
        _captureField = null;
        ShowShortcuts();
    }

    // The field being recorded keeps its prompt; the others show their saved shortcut.
    private void ShowShortcuts()
    {
        var settings = _session.State.Settings;
        if (_captureField != HotkeyField) HotkeyField.ShowShortcut(settings.Shortcut.DisplayText);
        if (_captureField != RecordHotkeyField) RecordHotkeyField.ShowShortcut(settings.RecordShortcut.DisplayText);
        if (_captureField != RecordPasteHotkeyField) RecordPasteHotkeyField.ShowShortcut(settings.RecordPasteShortcut.DisplayText);
    }
}
