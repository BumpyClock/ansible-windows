using System.Diagnostics;
using Ansible.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Ansible;

public sealed partial class MeetingWindow : Window
{
    private readonly MeetingSession _session;
    private readonly MeetingStore _store;
    private MeetingDocument? _document;
    private string? _shownNotice;
    private readonly bool _savedMeeting;
    private bool _closed;
    private bool _allowClose;
    private bool _busy;
    private Task _startTask = Task.CompletedTask;
    private bool _closing;
    private bool _shutdownPending;
    private long _version = -1;
    private IReadOnlyList<MeetingTurn>? _renderedTurns;
    private IReadOnlyDictionary<string, string>? _renderedNames;
    private IReadOnlyDictionary<string, string>? _renderedAutoNames;
    private CancellationTokenSource? _cleanupCancellation;
    private Task? _cleanupTask;
    private ContentDialog? _cleanupDialog;
    private string _cleanupModel = "gpt-6-luna";
    private string _cleanupEffort = "high";
    private bool? _capturePrivacy;
    private MeetingDetectionCandidate? _selectedDetectedMeeting;
    private bool _refreshingProcesses;
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
            AudioSourceChoice.SelectedIndex = 0;
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

    internal MeetingWindow(MeetingSession session, MeetingStore store, MeetingDocument? document = null)
    {
        InitializeComponent();
        _session = session;
        _store = store;
        _document = document;
        _savedMeeting = document is not null;
        Title = document?.Title ?? "New meeting";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 900));
        AudioSourceChoice.SelectedIndex = 0;
        RefreshProcesses();
        AppWindow.Closing += ClosingRequested;
        Closed += (_, _) =>
        {
            _closed = true;
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
        CaptureSetup.Visibility = active || _document is not null ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Visibility = active || _document is not null ? Visibility.Collapsed : Visibility.Visible;
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
        SaveText.Text = "Text saves automatically";
        EmptyTitle.Text = preparing ? "Preparing local transcription" : finishing ? "Finishing your meeting" : active ? "Listening for your conversation" : "Ready when you are";
        EmptyDescription.Text = state.Phase == MeetingSessionPhase.Paused
            ? "Capture is paused. Resume to continue this meeting."
            : finishing ? "Captured speech is being finalized locally. Speaker corrections preserve the recognized wording."
            : active ? "Text appears as each chunk is recognized. Speaker labels may be revised."
            : "Choose your meeting app below. Speaker-labelled text will appear here as you talk.";
        // The notice lives for the whole meeting; open it once per text so a dismissal sticks.
        if (state.SelectionNotice != _shownNotice)
        {
            _shownNotice = state.SelectionNotice;
            if (state.SelectionNotice is not null && state.Error is null) ShowNotice(state.SelectionNotice);
        }
        if (state.Error is not null) ShowError(state.Error);
        RenderDocument();
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
        var nearBottom = TranscriptScroll.ScrollableHeight - TranscriptScroll.VerticalOffset < 60;
        _renderedTurns = _document.Turns;
        _renderedNames = _document.SpeakerNames;
        _renderedAutoNames = _document.AutoSpeakerNames;
        TranscriptRows.Children.Clear();
        string? previousSpeaker = null;
        foreach (var turn in _document.Turns)
        {
            var block = new StackPanel { Spacing = 6 };
            if (previousSpeaker != turn.SpeakerId)
            {
                var heading = new Button
                {
                    Content = _document.GetSpeakerName(turn.SpeakerId),
                    Background = null, BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetName(heading, $"Rename speaker {_document.GetSpeakerName(turn.SpeakerId)}");
                var speakerId = turn.SpeakerId;
                heading.Click += async (_, _) => await RenameSpeakerAsync(speakerId);
                block.Children.Add(heading);
            }
            block.Children.Add(new TextBlock
            {
                Text = $"{FormatTurnTime(turn)}{(turn.IsProvisional ? " · provisional" : "")}{(HasCleanup(turn) ? " · wording edited" : "")}",
                Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"]
            });
            block.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                Child = new TextBlock { Text = turn.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 16 }
            });
            if (HasCleanup(turn))
                block.Children.Add(new Expander
                {
                    Header = "Original wording",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new TextBlock { Text = turn.OriginalText, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
                });
            TranscriptRows.Children.Add(block);
            previousSpeaker = turn.SpeakerId;
        }
        if (nearBottom) TranscriptScroll.UpdateLayout();
        if (nearBottom) TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null, true);
    }

    private async void StartClicked(object sender, RoutedEventArgs args)
    {
        if (_busy || _closing || _closed) return;
        _startTask = RunActionAsync(StartMeetingAsync);
        await _startTask;
    }

    private async Task StartMeetingAsync()
    {
        var system = AudioSourceChoice.SelectedIndex == 1;
        var process = (ProcessChoice.SelectedItem as ComboBoxItem)?.Tag as MeetingProcessChoice;
        if (!system && process is null) throw new InvalidOperationException("Select a meeting app, or explicitly choose all system audio.");
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
                throw new InvalidOperationException("The detected Teams process changed or closed. Select your meeting app again before starting.");
            }
        }
        var title = MeetingTitle.Text;
        await _session.StartAsync(new MeetingCaptureRequest(system ? MeetingPlaybackSource.SystemAudio : MeetingPlaybackSource.MeetingApp,
            system ? process is { IsTeams: true } ? process.Id : null : process?.Id));
        if (!string.IsNullOrWhiteSpace(title) && title != "New meeting") await _session.RenameAsync(title);
    }

    private async void PauseClicked(object sender, RoutedEventArgs args) => await RunActionAsync(() =>
        _session.State.Phase == MeetingSessionPhase.Paused ? _session.ResumeAsync() : _session.PauseAsync());
    private async void EndClicked(object sender, RoutedEventArgs args) => await RunActionAsync(() => _session.EndAsync());

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        ErrorBar.IsOpen = false;
        ErrorBar.Visibility = Visibility.Collapsed;
        StartButton.IsEnabled = PauseButton.IsEnabled = EndButton.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) { if (!_closed) ShowError(error.Message); }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                if (!_savedMeeting) Render(_session.State);
                StartButton.IsEnabled = true;
            }
        }
    }

    private async void TitleLostFocus(object sender, RoutedEventArgs args)
    {
        if (_document is null || MeetingTitle.Text.Trim() == _document.Title) return;
        try
        {
            if (_savedMeeting) _document = await _store.RenameAsync(_document.Id, MeetingTitle.Text);
            else await _session.RenameAsync(MeetingTitle.Text);
            RenderDocument();
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async Task RenameSpeakerAsync(string speakerId)
    {
        if (_document is null) return;
        var input = new TextBox { Text = _document.GetSpeakerName(speakerId), MaxLength = 100, Header = "Speaker name" };
        var dialog = new ContentDialog { Title = "Rename speaker", Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Root.XamlRoot };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (_savedMeeting) _document = await _store.RenameSpeakerAsync(_document.Id, speakerId, input.Text);
            else await _session.RenameSpeakerAsync(speakerId, input.Text);
            RenderDocument();
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
        if (_document is null || (!_savedMeeting && _session.IsActive) || _cleanupTask is { IsCompleted: false }) return;
        var dialog = new ContentDialog { Title = "Delete this meeting?", Content = "The saved transcript will be permanently deleted.", PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Root.XamlRoot };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await _store.DeleteAsync(_document.Id);
            _allowClose = true;
            Close();
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async void ClosingRequested(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _cleanupDialog?.Hide();
        if (!_allowClose && _cleanupTask is { IsCompleted: false })
        {
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            try
            {
                await CancelCleanupAsync();
                _allowClose = true;
                Close();
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
                _allowClose = true;
                Close();
            }
            catch (Exception error) { ShowError(error.Message); }
            finally { _closing = false; }
            return;
        }
        if (_busy) { args.Cancel = true; return; }
        if (_savedMeeting) return;
        if (!_session.IsActive) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
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
                _allowClose = true;
                Close();
            });
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { _closing = false; }
    }

    private bool CanCleanup => !_shutdownPending && _document is { Status: MeetingStatus.Completed, Turns.Count: > 0 } document &&
        !_session.IsActive && !document.Turns.Any(turn => turn.IsProvisional);
    private static bool HasCleanup(MeetingTurn turn) => turn.OriginalText is not null && turn.OriginalText != turn.Text;

    private void RefreshCleanupControls()
    {
        var running = _cleanupTask is { IsCompleted: false };
        CleanupButton.Visibility = CanCleanup && !running ? Visibility.Visible : Visibility.Collapsed;
        CleanupButton.IsEnabled = !_busy && !_closing;
        CancelCleanupButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RevertCleanupButton.Visibility = CanCleanup && !running && _document!.Turns.Any(HasCleanup) ? Visibility.Visible : Visibility.Collapsed;
        RevertCleanupButton.IsEnabled = !_busy && !_closing;
        if (running) DeleteMenuItem.IsEnabled = false;
    }

    private async void CleanupClicked(object sender, RoutedEventArgs args)
    {
        if (!CanCleanup || _busy || _closing || _cleanupTask is { IsCompleted: false }) return;
        var model = new TextBox { Header = "Copilot model", Text = _cleanupModel, MaxLength = 100 };
        var effort = new ComboBox { Header = "Reasoning effort", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" }) effort.Items.Add(value);
        effort.SelectedItem = _cleanupEffort;
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(new TextBlock
        {
            Text = "Transcript text is sent to GitHub Copilot using your signed-in account. Audio stays on this device. Speaker labels and timestamps are preserved. Original wording remains available.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(model);
        content.Children.Add(effort);
        var dialog = new ContentDialog
        {
            Title = "Clean up transcript wording", Content = content,
            PrimaryButtonText = "Send text and clean up", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = Root.XamlRoot
        };
        try
        {
            _cleanupDialog = dialog;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !CanCleanup || _closed || _closing) return;
            var options = new MeetingCleanupOptions(model.Text.Trim(), (string)effort.SelectedItem);
            options.Validate();
            _cleanupModel = options.Model;
            _cleanupEffort = options.ReasoningEffort;
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
        CleanupStatusText.Text = "Cleaning up wording with Copilot. Original text is kept until the complete result is ready.";
        ErrorBar.IsOpen = false;
        ErrorBar.Visibility = Visibility.Collapsed;
        try
        {
            var texts = await new CopilotMeetingTextCleanup().CleanAsync(document, options, token);
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
        if (_cleanupTask is not null) await _cleanupTask;
    }

    internal async Task BeginShutdownAsync()
    {
        _shutdownPending = true;
        RefreshCleanupControls();
        await CancelCleanupAsync();
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

    private void AudioSourceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ProcessChoice is null) return;
        var system = AudioSourceChoice.SelectedIndex == 1;
        ProcessChoice.Header = system ? "Teams app for speaker names (optional)" : "Meeting app";
        if (system && ProcessChoice.Items.Count > 0) ProcessChoice.SelectedIndex = 0;
        RefreshProcesses();
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
        var system = AudioSourceChoice.SelectedIndex == 1;
        ProcessChoice.Items.Add(new ComboBoxItem { Content = system ? "None — use model speaker labels" : "Select a meeting app" });
        foreach (var choice in choices.Where(p => !system || p.IsTeams).OrderByDescending(p => p.IsTeams).ThenBy(p => p.DisplayName))
        {
            var item = new ComboBoxItem { Content = choice.DisplayName, Tag = choice };
            ProcessChoice.Items.Add(item);
            if (selected is not null && choice.Id == selected.Id && choice.StartedUtc == selected.StartedUtc) ProcessChoice.SelectedItem = item;
        }
        if (ProcessChoice.SelectedItem is null)
            ProcessChoice.SelectedIndex = AudioSourceChoice.SelectedIndex == 0 && choices.Any(p => p.IsTeams) ? 1 : 0;
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
    private static string FormatTurnTime(MeetingTurn turn) => turn.TimingQuality switch
    {
        MeetingTimingQuality.Aligned => $"{FormatTime(turn.StartSeconds)} · ASR-aligned",
        MeetingTimingQuality.Chunk => $"≈ {FormatTime(turn.StartSeconds)} · approximate chunk time",
        _ => "Time unavailable"
    };
    private sealed record MeetingProcessChoice(int Id, string DisplayName, bool IsTeams, DateTime StartedUtc);
}
