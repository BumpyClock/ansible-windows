using System.Diagnostics;
using Ansible.Core;
using Microsoft.UI;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Ansible;

public sealed partial class MeetingWindow : Window
{
    private readonly MeetingSession _session;
    private readonly MeetingStore _store;
    private readonly string? _diagnosticsDirectory;
    private MeetingDocument? _document;
    private string? _shownNotice;
    private readonly bool _savedMeeting;
    private bool _closed;
    private bool _allowClose;
    private bool _busy;
    private Task _startTask = Task.CompletedTask;
    private Task _titleSaveTask = Task.CompletedTask;
    private bool _closing;
    private bool _shutdownPending;
    private long _version = -1;
    private IReadOnlyList<MeetingTurn>? _renderedTurns;
    private IReadOnlyDictionary<string, string>? _renderedNames;
    private IReadOnlyDictionary<string, string>? _renderedAutoNames;
    private readonly MeetingTranscriptProjection _transcript = new();
    private ScrollViewer? _transcriptScroll;
    private bool _followTranscriptBottom;
    private CancellationTokenSource? _cleanupCancellation;
    private Task? _cleanupTask;
    private ContentDialog? _cleanupDialog;
    private readonly IMeetingTextCleanup _textCleanup;
    private readonly Func<MeetingCleanupSettings> _getCleanupSettings;
    private string? _shownCleanupError;
    private bool? _capturePrivacy;
    private MeetingDetectionCandidate? _selectedDetectedMeeting;
    private bool _refreshingProcesses;
    private ThemeSettings? _themeSettings;
    internal MeetingDetectionCandidate? SelectedMeetingIdentity =>
        (ProcessChoice.SelectedItem as ComboBoxItem)?.Tag is MeetingProcessChoice { IsTeams: true } choice
            ? new MeetingDetectionCandidate(choice.Id, choice.StartedUtc) : null;

    internal bool SelectDetectedMeeting(MeetingDetectionCandidate candidate)
    {
        if (_savedMeeting || _document is not null || _session.IsActive || _closing || _closed || _shutdownPending) return false;
        try
        {
            using var process = Process.GetProcessById(candidate.ProcessId);
            if (process.StartTime.ToUniversalTime() != candidate.ProcessStartedUtc ||
                !(process.ProcessName.Equals("ms-teams", StringComparison.OrdinalIgnoreCase) ||
                  process.ProcessName.Equals("msteams", StringComparison.OrdinalIgnoreCase) ||
                  process.ProcessName.Equals("Teams", StringComparison.OrdinalIgnoreCase))) return false;
            RefreshProcesses();
            var item = ProcessChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                item.Tag is MeetingProcessChoice choice && choice.Id == candidate.ProcessId);
            // A valid Teams capture process may not own the app's visible top-level window.
            if (item is null)
            {
                item = new ComboBoxItem { Content = $"{process.ProcessName} · detected microphone activity",
                    Tag = new MeetingProcessChoice(process.Id, process.ProcessName, true, candidate.ProcessStartedUtc) };
                ProcessChoice.Items.Add(item);
            }
            ProcessChoice.SelectedItem = item;
            _selectedDetectedMeeting = candidate;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal MeetingWindow(MeetingSession session, MeetingStore store, MeetingDocument? document = null,
        ElementTheme theme = ElementTheme.Default, string? diagnosticsDirectory = null,
        IMeetingTextCleanup? textCleanup = null, Func<MeetingCleanupSettings>? getCleanupSettings = null)
    {
        InitializeComponent();
        Root.RequestedTheme = theme;
        TranscriptRows.ItemsSource = _transcript.Rows;
        _session = session;
        _store = store;
        _textCleanup = textCleanup ?? new MeetingCleanupService();
        _getCleanupSettings = getCleanupSettings ?? (() => new());
        _diagnosticsDirectory = diagnosticsDirectory;
        AudioDiagnosticsMenuItem.IsEnabled = diagnosticsDirectory is not null;
        _document = document;
        _savedMeeting = document is not null;
        Title = document?.Title ?? "New meeting";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
        _themeSettings.Changed += ContrastThemeChanged;
        RefreshTitleBarColors();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 900));
        RefreshProcesses();
        AppWindow.Closing += ClosingRequested;
        Closed += (_, _) =>
        {
            _closed = true;
            if (_themeSettings is not null) _themeSettings.Changed -= ContrastThemeChanged;
            _themeSettings = null;
            _session.StateChanged -= SessionChanged;
            _cleanupCancellation?.Dispose();
            _cleanupCancellation = null;
        };
        if (_savedMeeting) RenderDocument();
        else
        {
            _session.StateChanged += SessionChanged;
            Render(_session.State);
        }
        MeetingDate.Text = (_document?.CreatedAt ?? DateTimeOffset.Now).ToLocalTime().ToString("dddd, MMMM d · h:mm tt");
        CaptureSetup.Visibility = _savedMeeting ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Visibility = _savedMeeting ? Visibility.Collapsed : Visibility.Visible;
        if (_savedMeeting) PrivacyText.Text = "Saved on this device. No audio recording is stored.";
    }

    private void RootThemeChanged(FrameworkElement sender, object args) => RefreshTitleBarColors();
    private void ContrastThemeChanged(ThemeSettings sender, object args) => RefreshTitleBarColors();

    private void RefreshTitleBarColors() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) return;
        AppWindow.TitleBar.ButtonForegroundColor = ((SolidColorBrush)MeetingTitle.Foreground).Color;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = ((SolidColorBrush)TitleBarCaption.Foreground).Color;
    });

    private void SessionChanged(MeetingSessionSnapshot state) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closed && state.Version > _version) Render(state);
    });

    internal void ApplyCapturePrivacy(bool enabled)
    {
        if (_closed || _capturePrivacy == enabled) return;
        _capturePrivacy = enabled;
        try { Ansible.Core.Windows.WindowCapturePrivacy.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this), enabled); }
        catch (Exception error) { ShowError($"Screen capture privacy could not be updated. {error.Message}"); throw; }
    }

    private void Render(MeetingSessionSnapshot state)
    {
        _version = state.Version;
        _document = state.Document;
        var active = _session.IsActive || state.Phase is MeetingSessionPhase.Starting or MeetingSessionPhase.Recording or MeetingSessionPhase.Paused or MeetingSessionPhase.Finishing or MeetingSessionPhase.Refining;
        var preparing = state.Phase == MeetingSessionPhase.Starting;
        var finishing = state.Phase is MeetingSessionPhase.Finishing or MeetingSessionPhase.Refining;
        var retryStart = state.Phase == MeetingSessionPhase.Failed && !active && _document?.Turns.Count == 0;
        var canStart = !active && (_document is null || retryStart);
        CaptureSetup.Visibility = canStart ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = canStart ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Content = retryStart ? "Try again" : "Start meeting";
        PauseButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = state.Phase == MeetingSessionPhase.Paused ? "Resume" : "Pause";
        PauseButton.IsEnabled = !_busy && state.Phase is MeetingSessionPhase.Recording or MeetingSessionPhase.Paused;
        EndButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        EndButton.IsEnabled = !_busy && !finishing && !preparing;
        DeleteMenuItem.IsEnabled = !active && _document is not null;
        ElapsedText.Text = FormatTime(state.CapturedSeconds);
        StateText.Text = state.Phase switch
        {
            MeetingSessionPhase.Starting => "Preparing",
            MeetingSessionPhase.Recording => "Transcribing",
            MeetingSessionPhase.Paused => "Paused",
            MeetingSessionPhase.Finishing => "Finishing transcript",
            MeetingSessionPhase.Refining => "Refining speakers",
            MeetingSessionPhase.Completed => "Completed",
            MeetingSessionPhase.Failed => "Stopped",
            _ => "Ready"
        };
        DelayText.Visibility = state.PendingSeconds >= 10 ? Visibility.Visible : Visibility.Collapsed;
        DelayText.Text = $"{FormatTime(state.PendingSeconds)} of captured audio awaiting transcription";
        var health = state.CaptureHealth;
        AudioHealthPanel.Visibility = state.Phase == MeetingSessionPhase.Recording && health is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (health is not null)
        {
            MicrophoneHealthText.Text = $"Microphone: {health.Microphone.SignalStatus}";
            var devices = health.OutputDevices == 1 ? "1 device" : $"{health.OutputDevices} devices";
            PlaybackHealthText.Text = health.OutputDevices == 0
                ? "Speakers and headphones: No output device available"
                : $"Speakers and headphones ({devices}): {health.Playback.SignalStatus}";
            PlaybackHintText.Visibility = health.OutputDevices == 0 || health.PlaybackIsQuiet ? Visibility.Visible : Visibility.Collapsed;
            PlaybackHintText.Text = health.OutputDevices == 0
                ? "No active speakers or headphones can be recorded, so only your microphone is transcribed. Capture resumes when an output device becomes available."
                : health.SkippedOutputDevices > 0
                    ? "No playback detected for 15 seconds. Some output devices are in exclusive use by another app and cannot be recorded. Audio diagnostics can help identify the cause."
                    : "No playback detected for 15 seconds. Silence is normal when others are quiet. If you can hear them, audio diagnostics can help identify the cause.";
            DiagnosticsErrorText.Text = health.DiagnosticsError ?? "";
            DiagnosticsErrorText.Visibility = health.DiagnosticsError is null ? Visibility.Collapsed : Visibility.Visible;
        }
        SaveText.Text = "Text saves automatically";
        (EmptyTitle.Text, EmptyDescription.Text) = state.Phase switch
        {
            MeetingSessionPhase.Starting => ("Preparing local transcription", "The meeting will begin when local transcription is ready."),
            MeetingSessionPhase.Paused => ("Meeting paused", "Capture is paused. Resume to continue this meeting."),
            MeetingSessionPhase.Recording => ("Listening for your conversation", "Text appears as each chunk is recognized. Speaker labels may be revised."),
            MeetingSessionPhase.Finishing or MeetingSessionPhase.Refining => ("Finishing your meeting", "Captured speech is being finalized locally. Speaker corrections preserve the recognized wording."),
            MeetingSessionPhase.Failed when retryStart => ("Meeting needs attention", "Resolve the error above, check your microphone and speakers, then try again."),
            MeetingSessionPhase.Failed => ("Meeting stopped", "Review the error above. Text already transcribed stays saved on this device."),
            MeetingSessionPhase.Completed => ("No speech transcribed", "This meeting has no recognized speech. Saved text stays on this device."),
            _ => ("Ready when you are", "Start the meeting to see speaker-labelled text here.")
        };
        // The notice lives for the whole meeting; open it once per text so a dismissal sticks.
        if (state.SelectionNotice != _shownNotice)
        {
            _shownNotice = state.SelectionNotice;
            if (state.SelectionNotice is not null && state.Error is null) ShowNotice(state.SelectionNotice);
        }
        if (state.Error is not null) ShowError(state.Error);
        if (state.TextCleanup != MeetingTextCleanupState.None)
        {
            CleanupStatusText.Visibility = Visibility.Visible;
            CleanupStatusText.Text = state.TextCleanup switch
            {
                MeetingTextCleanupState.Running => "Cleaning up wording automatically. Original text is saved.",
                MeetingTextCleanupState.Completed => "Wording cleaned up. Original text remains available.",
                MeetingTextCleanupState.Cancelled => "Cleanup cancelled. Original wording remains available.",
                MeetingTextCleanupState.Failed => "Cleanup failed. Original wording is saved. Check Settings or retry cleanup.",
                _ => ""
            };
            if (state.TextCleanupError is { } cleanupError && cleanupError != _shownCleanupError)
            {
                _shownCleanupError = cleanupError;
                ShowError(cleanupError);
            }
        }
        RenderDocument();
    }

    private void AudioDiagnosticsClicked(object sender, RoutedEventArgs args)
    {
        if (_diagnosticsDirectory is null) return;
        try
        {
            if (!Directory.Exists(_diagnosticsDirectory))
            {
                ShowError("No local diagnostics have been saved. Enable Local diagnostics in Settings to collect them.");
                return;
            }
            Process.Start(new ProcessStartInfo(_diagnosticsDirectory) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError($"Audio diagnostics could not be opened. {error.Message}");
        }
    }

    private void RenderDocument()
    {
        RefreshCleanupControls();
        if (_document is null) return;
        if (MeetingTitle.FocusState == FocusState.Unfocused) MeetingTitle.Text = _document.Title;
        Title = _document.Title;
        EmptyState.Visibility = _document.Turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_savedMeeting)
        {
            StateText.Text = _document.Status.ToString();
            ElapsedText.Text = FormatTime(_document.Turns.LastOrDefault()?.EndSeconds ?? 0);
            EmptyTitle.Text = "No transcript text";
            EmptyDescription.Text = "This meeting has no saved speech. Audio was not stored.";
        }
        if (ReferenceEquals(_renderedTurns, _document.Turns) && ReferenceEquals(_renderedNames, _document.SpeakerNames) && ReferenceEquals(_renderedAutoNames, _document.AutoSpeakerNames)) return;
        var nearBottom = _transcriptScroll is null || _transcriptScroll.ScrollableHeight - _transcriptScroll.VerticalOffset < 60;
        _renderedTurns = _document.Turns;
        _renderedNames = _document.SpeakerNames;
        _renderedAutoNames = _document.AutoSpeakerNames;
        if (_transcript.Reconcile(_document) && nearBottom) _followTranscriptBottom = true;
    }

    public static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void SpeakerClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string speakerId }) await RenameSpeakerAsync(speakerId);
    }

    private void TranscriptLoaded(object sender, RoutedEventArgs args) => _transcriptScroll = FindTranscriptScroll(TranscriptRows);

    private static ScrollViewer? FindTranscriptScroll(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroll) return scroll;
            if (FindTranscriptScroll(child) is { } nested) return nested;
        }
        return null;
    }

    private void TranscriptLayoutUpdated(object? sender, object args)
    {
        if (!_followTranscriptBottom || _transcriptScroll is null) return;
        _followTranscriptBottom = false;
        // Follow only after normal layout has computed the extent. Never force layout for
        // every transcript update, or move a reader who is reviewing earlier speech.
        _transcriptScroll.ChangeView(null, _transcriptScroll.ScrollableHeight, null, true);
    }

    private async void StartClicked(object sender, RoutedEventArgs args)
    {
        if (_busy || _closing || _closed) return;
        _startTask = RunActionAsync(StartMeetingAsync);
        await _startTask;
    }

    private async Task StartMeetingAsync()
    {
        var process = (ProcessChoice.SelectedItem as ComboBoxItem)?.Tag as MeetingProcessChoice;
        if (_selectedDetectedMeeting is { } detected)
        {
            var valid = false;
            try
            {
                using var current = Process.GetProcessById(detected.ProcessId);
                valid = current.StartTime.ToUniversalTime() == detected.ProcessStartedUtc &&
                    (current.ProcessName.Equals("ms-teams", StringComparison.OrdinalIgnoreCase) ||
                     current.ProcessName.Equals("msteams", StringComparison.OrdinalIgnoreCase) ||
                     current.ProcessName.Equals("Teams", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (!valid)
            {
                _selectedDetectedMeeting = null;
                ProcessChoice.SelectedIndex = 0;
                throw new InvalidOperationException("The detected Teams process changed or closed. Refresh the Teams selection before starting.");
            }
        }
        var title = MeetingTitle.Text;
        await _session.StartAsync(new MeetingCaptureRequest(process is { IsTeams: true } ? process.Id : null));
        if (!string.IsNullOrWhiteSpace(title) && title != "New meeting") await _session.RenameAsync(title);
    }

    private async void PauseClicked(object sender, RoutedEventArgs args) => await RunActionAsync(() =>
        _session.State.Phase == MeetingSessionPhase.Paused ? _session.ResumeAsync() : _session.PauseAsync());
    private async void EndClicked(object sender, RoutedEventArgs args) => await RunActionAsync(() => _session.EndAsync());

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        string? actionError = null;
        ErrorBar.IsOpen = false;
        ErrorBar.Visibility = Visibility.Collapsed;
        StartButton.IsEnabled = PauseButton.IsEnabled = EndButton.IsEnabled = false;
        RefreshCleanupControls();
        try
        {
            await _titleSaveTask;
            await action();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) { actionError = error.Message; }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                if (!_savedMeeting) Render(_session.State);
                if (actionError is not null) ShowError(actionError);
                StartButton.IsEnabled = true;
                RefreshCleanupControls();
            }
        }
    }

    private async void TitleLostFocus(object sender, RoutedEventArgs args)
    {
        if (_document is null || _busy || _closing || _shutdownPending || MeetingTitle.Text.Trim() == _document.Title) return;
        _titleSaveTask = SaveTitleAsync(_titleSaveTask, MeetingTitle.Text);
        await _titleSaveTask;
    }

    private async Task SaveTitleAsync(Task previous, string title)
    {
        await previous;
        try
        {
            if (_savedMeeting) _document = await _store.RenameAsync(_document!.Id, title);
            else await _session.RenameAsync(title);
            RenderDocument();
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async Task RenameSpeakerAsync(string speakerId)
    {
        if (_document is null || _busy || _closing || _shutdownPending) return;
        var input = new TextBox { Text = _document.GetSpeakerName(speakerId), MaxLength = 100, Header = "Speaker name" };
        var dialog = new ContentDialog { Title = "Rename speaker", Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Root.XamlRoot };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closing || _closed || _shutdownPending) return;
            await RunActionAsync(async () =>
            {
                if (_savedMeeting) _document = await _store.RenameSpeakerAsync(_document.Id, speakerId, input.Text);
                else await _session.RenameSpeakerAsync(speakerId, input.Text);
                RenderDocument();
            });
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void CopyClicked(object sender, RoutedEventArgs args)
    {
        if (_document is null) return;
        try
        {
            var data = new DataPackage();
            data.SetText(MeetingStore.ExportText(_document));
            Clipboard.SetContent(data);
            SaveText.Text = "Transcript copied";
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async void ExportClicked(object sender, RoutedEventArgs args)
    {
        if (_document is null) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "Meeting transcript" };
            picker.FileTypeChoices.Add("Text document", [".txt"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is not null) { await FileIO.WriteTextAsync(file, MeetingStore.ExportText(_document)); SaveText.Text = "Transcript exported"; }
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async void DeleteClicked(object sender, RoutedEventArgs args)
    {
        if (_document is null || _busy || _closing || _shutdownPending || (!_savedMeeting && _session.IsActive) || _cleanupTask is { IsCompleted: false }) return;
        var dialog = new ContentDialog { Title = "Delete this meeting?", Content = "The saved transcript will be permanently deleted.", PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Root.XamlRoot };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closing || _closed || _shutdownPending) return;
            await RunActionAsync(async () =>
            {
                await _session.EndAsync();
                await _store.DeleteAsync(_document.Id);
                await CloseAfterSavingAsync();
            });
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async void ClosingRequested(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _cleanupDialog?.Hide();
        if (!_allowClose && (_cleanupTask is { IsCompleted: false } || _session.State.TextCleanup == MeetingTextCleanupState.Running))
        {
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            try
            {
                await CancelCleanupAsync();
                await CloseAfterSavingAsync();
            }
            catch (Exception error) { ShowError(error.Message); }
            finally { _closing = false; }
            return;
        }
        if (_allowClose) return;
        if (!_savedMeeting && _session.State.Phase == MeetingSessionPhase.Starting)
        {
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            try
            {
                await _session.DiscardAsync();
                await _startTask;
                await CloseAfterSavingAsync();
            }
            catch (Exception error) { ShowError(error.Message); }
            finally { _closing = false; }
            return;
        }
        if (_busy) { args.Cancel = true; return; }
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        if (_savedMeeting || !_session.IsActive)
        {
            try { await CloseAfterSavingAsync(); }
            catch (Exception error) { ShowError(error.Message); }
            finally { _closing = false; }
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "End this meeting before closing?",
            Content = "Finish captured speech and save the transcript, or discard speech still awaiting transcription. Saved text is kept.",
            PrimaryButtonText = "Finish and close", SecondaryButtonText = "Discard pending speech", CloseButtonText = "Keep open",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Root.XamlRoot
        };
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) return;
            await RunActionAsync(async () =>
            {
                if (result == ContentDialogResult.Primary) await _session.EndAsync();
                else await _session.DiscardAsync();
                await CloseAfterSavingAsync();
            });
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { _closing = false; }
    }

    private async Task CloseAfterSavingAsync()
    {
        await _titleSaveTask;
        await _session.DisposeAsync();
        _allowClose = true;
        Close();
    }

    private bool CanCleanup => !_shutdownPending && _document is { Status: MeetingStatus.Completed, Turns.Count: > 0 } document &&
        !_session.IsActive && _session.State.TextCleanup != MeetingTextCleanupState.Running && !document.Turns.Any(turn => turn.IsProvisional);
    private static bool HasCleanup(MeetingTurn turn) => turn.OriginalText is not null && turn.OriginalText != turn.Text;

    private void RefreshCleanupControls()
    {
        var running = _cleanupTask is { IsCompleted: false } || _session.State.TextCleanup == MeetingTextCleanupState.Running;
        CleanupButton.Visibility = CanCleanup && !running ? Visibility.Visible : Visibility.Collapsed;
        CleanupButton.IsEnabled = !_busy && !_closing;
        CancelCleanupButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RevertCleanupButton.Visibility = CanCleanup && !running && _document!.Turns.Any(HasCleanup) ? Visibility.Visible : Visibility.Collapsed;
        RevertCleanupButton.IsEnabled = !_busy && !_closing;
        DeleteMenuItem.IsEnabled = !running && !_busy && !_closing && !_shutdownPending && !_session.IsActive && _document is not null;
    }

    private async void CleanupClicked(object sender, RoutedEventArgs args)
    {
        if (!CanCleanup || _busy || _closing || _cleanupTask is { IsCompleted: false }) return;
        MeetingCleanupOptions options;
        try { options = _getCleanupSettings().Options; options.Validate(); }
        catch (Exception error) { ShowError($"Check Transcript cleanup in Settings. {error.Message}"); return; }
        var provider = options.Provider == MeetingCleanupProvider.CopilotCli ? "GitHub Copilot CLI using your signed-in account" : "the OpenAI API using your saved API key";
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(new TextBlock
        {
            Text = $"Transcript text will be sent to {provider}. Model: {options.Model}. Reasoning effort: {options.ReasoningEffort}. Change the connection in Settings. Audio stays on this device. Speaker labels, timestamps, and original wording are preserved.",
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            Title = "Clean up transcript wording", Content = content,
            PrimaryButtonText = "Send text and clean up", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = Root.XamlRoot
        };
        try
        {
            _cleanupDialog = dialog;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !CanCleanup || _busy || _closed || _closing) return;
            _cleanupCancellation?.Dispose();
            _cleanupCancellation = new CancellationTokenSource();
            _cleanupTask = CleanWordingAsync(_document!, options, _cleanupCancellation.Token);
            RefreshCleanupControls();
            await _cleanupTask;
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { _cleanupDialog = null; RefreshCleanupControls(); }
    }

    private async Task CleanWordingAsync(MeetingDocument document, MeetingCleanupOptions options, CancellationToken token)
    {
        CleanupStatusText.Visibility = Visibility.Visible;
        CleanupStatusText.Text = "Cleaning up wording. Original text is kept until the complete result is ready.";
        ErrorBar.IsOpen = false;
        ErrorBar.Visibility = Visibility.Collapsed;
        try
        {
            var texts = await _textCleanup.CleanAsync(document, options, token);
            token.ThrowIfCancellationRequested();
            if (_savedMeeting) _document = await _store.ApplyCleanupAsync(document.Id, texts, token);
            else
            {
                await _session.ApplyCleanupAsync(texts);
                _document = _session.Document;
            }
            CleanupStatusText.Text = "Wording cleaned up. Original text remains available.";
            RenderDocument();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CleanupStatusText.Text = "Cleanup cancelled. Transcript unchanged.";
        }
        catch (Exception error)
        {
            CleanupStatusText.Text = "Cleanup could not be completed. Original wording remains available.";
            ShowError(error.Message);
        }
        finally
        {
            DeleteMenuItem.IsEnabled = !_session.IsActive && _document is not null;
        }
    }

    private async void CancelCleanupClicked(object sender, RoutedEventArgs args)
    {
        try { await CancelCleanupAsync(); }
        catch (Exception error) { ShowError(error.Message); }
        finally { RefreshCleanupControls(); }
    }

    internal async Task CancelCleanupAsync()
    {
        _cleanupDialog?.Hide();
        _cleanupCancellation?.Cancel();
        _session.CancelTextCleanup();
        if (_session.State.TextCleanup == MeetingTextCleanupState.Running) await _session.EndAsync();
        if (_cleanupTask is not null) await _cleanupTask;
    }

    internal async Task BeginShutdownAsync()
    {
        _shutdownPending = true;
        RefreshCleanupControls();
        await CancelCleanupAsync();
        await _titleSaveTask;
    }

    private async void RevertCleanupClicked(object sender, RoutedEventArgs args)
    {
        if (!CanCleanup || _busy || _closing || _cleanupTask is { IsCompleted: false }) return;
        _cleanupCancellation?.Dispose();
        _cleanupCancellation = new CancellationTokenSource();
        _cleanupTask = RestoreWordingAsync(_cleanupCancellation.Token);
        RefreshCleanupControls();
        await _cleanupTask;
        RefreshCleanupControls();
    }

    private async Task RestoreWordingAsync(CancellationToken token)
    {
        CleanupStatusText.Visibility = Visibility.Visible;
        CleanupStatusText.Text = "Restoring original wording.";
        try
        {
            if (_savedMeeting) _document = await _store.RevertCleanupAsync(_document!.Id, token);
            else
            {
                token.ThrowIfCancellationRequested();
                await _session.ApplyCleanupAsync(_document!.Turns.Select(turn => turn.OriginalText ?? turn.Text).ToArray());
                _document = _session.Document;
            }
            CleanupStatusText.Text = "Original wording restored.";
            RenderDocument();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CleanupStatusText.Text = "Restore cancelled.";
        }
        catch (Exception error)
        {
            CleanupStatusText.Text = "Original wording could not be restored.";
            ShowError(error.Message);
        }
        finally { DeleteMenuItem.IsEnabled = !_session.IsActive && _document is not null; }
    }

    private void MeetingProcessChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshingProcesses && _selectedDetectedMeeting != SelectedMeetingIdentity) _selectedDetectedMeeting = null;
        UpdateNamingHelp();
    }
    private void UpdateNamingHelp()
    {
        if (NamingHelpText is null) return;
        var teams = (ProcessChoice.SelectedItem as ComboBoxItem)?.Tag is MeetingProcessChoice { IsTeams: true };
        NamingHelpText.Text = teams
            ? "Teams speaker names are applied only when confidently matched. Click any speaker label to correct it."
            : "Automatic names require a selected Teams app. Speakers keep editable model labels when names are unavailable.";
    }
    private void RefreshProcessesClicked(object sender, RoutedEventArgs args) => RefreshProcesses();
    private void RefreshProcesses()
    {
        var selected = (ProcessChoice.SelectedItem as ComboBoxItem)?.Tag as MeetingProcessChoice;
        var choices = new List<MeetingProcessChoice>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                        choices.Add(new MeetingProcessChoice(process.Id, $"{process.ProcessName} · {process.MainWindowTitle}",
                            process.ProcessName.Equals("ms-teams", StringComparison.OrdinalIgnoreCase) ||
                            process.ProcessName.Equals("msteams", StringComparison.OrdinalIgnoreCase) ||
                            process.ProcessName.Equals("Teams", StringComparison.OrdinalIgnoreCase), process.StartTime.ToUniversalTime()));
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        _refreshingProcesses = true;
        ProcessChoice.Items.Clear();
        ProcessChoice.Items.Add(new ComboBoxItem { Content = "None, use model speaker labels" });
        foreach (var choice in choices.Where(p => p.IsTeams).OrderBy(p => p.DisplayName))
        {
            var item = new ComboBoxItem { Content = choice.DisplayName, Tag = choice };
            ProcessChoice.Items.Add(item);
            if (selected is not null && choice.Id == selected.Id && choice.StartedUtc == selected.StartedUtc) ProcessChoice.SelectedItem = item;
        }
        if (ProcessChoice.SelectedItem is null)
            ProcessChoice.SelectedIndex = choices.Any(p => p.IsTeams) ? 1 : 0;
        _refreshingProcesses = false;
        if (_selectedDetectedMeeting != SelectedMeetingIdentity) _selectedDetectedMeeting = null;
        UpdateNamingHelp();
    }
    private void ShowError(string message) => ShowBar(InfoBarSeverity.Error, "Meeting needs attention", message);
    private void ShowNotice(string message) => ShowBar(InfoBarSeverity.Warning, "Speaker model changed", message);
    private void ShowBar(InfoBarSeverity severity, string title, string message)
    {
        ErrorBar.Severity = severity;
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.Visibility = Visibility.Visible;
        ErrorBar.IsOpen = true;
    }
    private void ErrorBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (!sender.IsOpen) sender.Visibility = Visibility.Collapsed;
    }
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private sealed record MeetingProcessChoice(int Id, string DisplayName, bool IsTeams, DateTime StartedUtc);
}
