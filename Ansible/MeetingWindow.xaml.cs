using System.Diagnostics;
using Ansible.Core;
using Microsoft.UI;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Ansible;

public sealed partial class MeetingWindow : Window
{
    internal bool DeleteOnClose { get; private set; }
    private readonly MeetingSession _session;
    private readonly MeetingStore _store;
    private readonly string? _diagnosticsDirectory;
    private const string NoticeKey = "meeting";
    private readonly AppNotices _notices = new();
    private MeetingDocument? _document;
    private string? _requestedTitle;
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
                item = new ComboBoxItem { Content = $"{process.ProcessName} · using your microphone",
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
        Notices.Attach(_notices);
        var transcriptPadding = TranscriptRows.Padding;
        Notices.CoveredHeightChanged += (_, _) => TranscriptRows.Padding =
            transcriptPadding with { Bottom = transcriptPadding.Bottom + Notices.CoveredHeight };
        TranscriptRows.ItemsSource = _transcript.Rows;
        _session = session;
        _store = store;
        _textCleanup = textCleanup ?? new MeetingCleanupService();
        _getCleanupSettings = getCleanupSettings ?? (() => new());
        _diagnosticsDirectory = diagnosticsDirectory;
        AudioDiagnosticsMenuItem.IsEnabled = diagnosticsDirectory is not null;
        _document = document;
        _savedMeeting = document is not null;
        RenderTitle();
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
        Root.Loaded += (_, _) => (_savedMeeting ? (Control)TranscriptRows : StartButton).Focus(FocusState.Programmatic);
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
        if (_savedMeeting)
        {
            CaptureSetup.Visibility = StartButton.Visibility = Visibility.Collapsed;
            SaveText.Text = SavedText;
            RenderDocument();
        }
        else
        {
            _session.StateChanged += SessionChanged;
            Render(_session.State);
        }
    }

    private const string SavedText = "Saved on this device";

    private void RenderTitle()
    {
        var title = _requestedTitle ?? (_document is null
            ? MeetingDocument.UntitledTitle
            : MeetingText.Title(MeetingSummary.From(_document)));
        MeetingTitle.Text = TitleBarCaption.Text = Title = title;
        AutomationProperties.SetName(TitleButton, $"Rename meeting {title}");
        UpdateTitleToolTip();
    }

    private void MeetingTitleTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => UpdateTitleToolTip();

    private void UpdateTitleToolTip() =>
        ToolTipService.SetToolTip(TitleButton, MeetingTitle.IsTextTrimmed ? MeetingTitle.Text : "Rename meeting");

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
        if (state.Phase == MeetingSessionPhase.Completed) _notices.Set("teams-context", null);
        _version = state.Version;
        _document = state.Document;
        if (state.Phase is not (MeetingSessionPhase.Starting or MeetingSessionPhase.Recording or MeetingSessionPhase.Paused))
            _contextStop?.Cancel();
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
        var elapsed = FormatTime(state.CapturedSeconds);
        ShowDetails(state.Phase switch
        {
            MeetingSessionPhase.Starting => "Getting ready",
            MeetingSessionPhase.Recording => $"Recording · {elapsed}",
            MeetingSessionPhase.Paused => $"Paused · {elapsed}",
            MeetingSessionPhase.Finishing => "Finishing transcript",
            MeetingSessionPhase.Refining => "Identifying speakers",
            MeetingSessionPhase.Completed when _document is not null => SavedDetails(_document),
            MeetingSessionPhase.Failed => "Stopped",
            _ => ""
        }, recording: state.Phase == MeetingSessionPhase.Recording);
        DelayText.Visibility = state.PendingSeconds >= 10 ? Visibility.Visible : Visibility.Collapsed;
        DelayText.Text = $"Catching up: {FormatTime(state.PendingSeconds)} of speech still to transcribe";
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
        SaveText.Text = canStart ? "" : state.Phase is MeetingSessionPhase.Completed or MeetingSessionPhase.Failed
            ? SavedText : "Saving as you go";
        (TranscriptEmpty.Glyph, TranscriptEmpty.Title, TranscriptEmpty.Detail) = state.Phase switch
        {
            MeetingSessionPhase.Starting => (MicrophoneGlyph, "Getting ready", "Transcription starts as soon as the speech model is loaded."),
            MeetingSessionPhase.Paused => (PauseGlyph, "Meeting paused", "Resume to keep transcribing this meeting."),
            MeetingSessionPhase.Recording => (MicrophoneGlyph, "Listening", "Text appears a few seconds after someone speaks. Speaker names may update."),
            MeetingSessionPhase.Finishing or MeetingSessionPhase.Refining => (MicrophoneGlyph, "Finishing your meeting", "The last few seconds of speech are being transcribed on this device."),
            MeetingSessionPhase.Failed when retryStart => (ErrorGlyph, "Meeting couldn't start", "Check the message below and your microphone and speakers, then try again."),
            MeetingSessionPhase.Failed => (ErrorGlyph, "Meeting stopped", "Check the message below. Text transcribed before the stop is saved."),
            MeetingSessionPhase.Completed => (MicrophoneGlyph, "No speech transcribed", MeetingText.EmptyExplanation(MeetingStatus.Completed)),
            _ => (MicrophoneGlyph, "Ready when you are", "Start the meeting to see what each person says here.")
        };
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
                MeetingTextCleanupState.Running => "Cleaning up wording automatically. The original wording is kept.",
                MeetingTextCleanupState.Completed => "Wording cleaned up. The original wording is kept.",
                MeetingTextCleanupState.Cancelled => "Cleanup cancelled. The transcript is unchanged.",
                MeetingTextCleanupState.Failed => "Cleanup didn't finish. The transcript is unchanged.",
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
        if (_document.Title == _requestedTitle) _requestedTitle = null;
        RenderTitle();
        TranscriptEmpty.Visibility = _document.Turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_savedMeeting)
        {
            ShowDetails(SavedDetails(_document), recording: false);
            TranscriptEmpty.Glyph = _document.Status == MeetingStatus.Failed ? ErrorGlyph : MicrophoneGlyph;
            TranscriptEmpty.Title = "No speech transcribed";
            TranscriptEmpty.Detail = MeetingText.EmptyExplanation(_document.Status);
            TranscriptEmpty.Footer ??= CreateEmptyDeleteButton();
        }
        if (ReferenceEquals(_renderedTurns, _document.Turns) && ReferenceEquals(_renderedNames, _document.SpeakerNames) && ReferenceEquals(_renderedAutoNames, _document.AutoSpeakerNames)) return;
        var nearBottom = _transcriptScroll is null || _transcriptScroll.ScrollableHeight - _transcriptScroll.VerticalOffset < 60;
        _renderedTurns = _document.Turns;
        _renderedNames = _document.SpeakerNames;
        _renderedAutoNames = _document.AutoSpeakerNames;
        if (_transcript.Reconcile(_document) && nearBottom && !_savedMeeting) _followTranscriptBottom = true;
    }

    private static string SavedDetails(MeetingDocument document) =>
        MeetingText.SavedDetails(MeetingSummary.From(document), DateOnly.FromDateTime(DateTime.Today));

    private void ShowDetails(string details, bool recording)
    {
        MeetingDetails.Text = details;
        DetailsRow.Visibility = details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecordingDot.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
    }

    private const string MicrophoneGlyph = "";
    private const string PauseGlyph = "";
    private const string ErrorGlyph = "";

    private Button CreateEmptyDeleteButton()
    {
        var button = new Button { Content = "Delete meeting" };
        button.Click += DeleteClicked;
        return button;
    }


    private async void SpeakerClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string speakerId }) await RenameSpeakerAsync(speakerId);
    }

    private void OriginalToggleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: MeetingTranscriptRow row }) row.IsOriginalExpanded = !row.IsOriginalExpanded;
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
        await StopFollowingTeamsAsync();
        _notices.Set("teams-context", null);
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
        _requestedTitle ??= _document is { IsUntitled: false } failed ? failed.Title : null;
        var title = _requestedTitle;
        var identity = process is { IsTeams: true } ? new MeetingDetectionCandidate(process.Id, process.StartedUtc) : null;
        var context = identity is null ? new MeetingContextObservation(MeetingCallPresence.Unknown)
            : await _teamsContext.ReadAsync(identity, CancellationToken.None);
        if (_closing || _closed || _shutdownPending) return;
        await _session.StartAsync(new MeetingCaptureRequest(process is { IsTeams: true } ? process.Id : null));
        if (title is not null) await _session.RenameAsync(title);
        if (identity is not null && !_shutdownPending && !_closing)
            FollowTeamsCall(identity, context);
    }

    private async void PauseClicked(object sender, RoutedEventArgs args) => await RunActionAsync(() =>
        _session.State.Phase == MeetingSessionPhase.Paused ? _session.ResumeAsync() : _session.PauseAsync());
    private async void EndClicked(object sender, RoutedEventArgs args) => await RunActionAsync(() => _session.EndAsync());

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        string? actionError = null;
        HideNotice();
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

    private async void RenameTitleClicked(object sender, RoutedEventArgs args)
    {
        if (_busy || _closing || _closed || _shutdownPending) return;
        var current = _requestedTitle ?? (_document is { IsUntitled: false } document ? document.Title : "");
        var input = new TextBox { Text = current, PlaceholderText = MeetingTitle.Text, MaxLength = 200, Header = "Meeting name" };
        var dialog = new ContentDialog { Title = "Rename meeting", Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        dialog.FollowWindowTheme(Root.XamlRoot);
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closing || _closed || _shutdownPending) return;
            var title = input.Text.Trim();
            if (title.Length == 0 || title == current) return;
            if (_document is null)
            {
                _requestedTitle = title;
                RenderTitle();
                return;
            }
            _requestedTitle = null;
            _titleSaveTask = SaveTitleAsync(_titleSaveTask, title);
            await _titleSaveTask;
        }
        catch (Exception error) { ShowError(error.Message); }
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
        var dialog = new ContentDialog { Title = "Rename speaker", Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        dialog.FollowWindowTheme(Root.XamlRoot);
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
        var dialog = MeetingDeletion.Dialog(1, Root.XamlRoot);
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closing || _closed || _shutdownPending) return;
            await RunActionAsync(async () =>
            {
                await CloseAfterSavingAsync(delete: true);
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
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.FollowWindowTheme(Root.XamlRoot);
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

    private async Task CloseAfterSavingAsync(bool delete = false)
    {
        await StopFollowingTeamsAsync();
        await _titleSaveTask;
        await _session.DisposeAsync();
        DeleteOnClose = delete;
        _allowClose = true;
        try { Close(); }
        catch { DeleteOnClose = false; throw; }
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
        RevertCleanupMenuItem.Visibility = CanCleanup && !running && _document!.Turns.Any(HasCleanup) ? Visibility.Visible : Visibility.Collapsed;
        RevertCleanupMenuItem.IsEnabled = !_busy && !_closing;
        CopyMenuItem.IsEnabled = ExportMenuItem.IsEnabled = _document is { Turns.Count: > 0 };
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
            DefaultButton = ContentDialogButton.Close
        };
        dialog.FollowWindowTheme(Root.XamlRoot);
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
        CleanupStatusText.Text = "Cleaning up wording. The transcript changes only when the whole result is ready.";
        HideNotice();
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
            CleanupStatusText.Text = "Wording cleaned up. The original wording is kept.";
            RenderDocument();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CleanupStatusText.Text = "Cleanup cancelled. The transcript is unchanged.";
        }
        catch (Exception error)
        {
            CleanupStatusText.Text = "Cleanup didn't finish. The transcript is unchanged.";
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
        _contextStop?.Cancel();
        RefreshCleanupControls();
        await CancelCleanupAsync();
        await StopFollowingTeamsAsync();
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
            CleanupStatusText.Text = "Restore cancelled. The transcript is unchanged.";
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
            ? "Uses the call's title, names speakers when it can match them, and ends when the call ends."
            : "Choose a Teams call to use its title and speaker names and to end with the call. Otherwise, select End meeting when you're done.";
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
        ProcessChoice.Items.Add(new ComboBoxItem { Content = "None" });
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
    private void ShowError(string message) =>
        _notices.Set(NoticeKey, new NoticeContent(NoticeSeverity.Error, "Meeting needs attention", message));
    private void ShowNotice(string message) =>
        _notices.Set(NoticeKey, new NoticeContent(NoticeSeverity.Warning, "Speaker model changed", message));
    private void HideNotice() => _notices.Set(NoticeKey, null);
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private sealed record MeetingProcessChoice(int Id, string DisplayName, bool IsTeams, DateTime StartedUtc);
}
