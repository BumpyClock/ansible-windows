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
    private readonly bool _savedMeeting;
    private bool _closed;
    private bool _allowClose;
    private bool _busy;
    private bool _closing;
    private long _version = -1;
    private IReadOnlyList<MeetingTurn>? _renderedTurns;
    private IReadOnlyDictionary<string, string>? _renderedNames;

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
        Closed += (_, _) => { _closed = true; _session.StateChanged -= SessionChanged; };
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

    private void Render(MeetingSessionSnapshot state)
    {
        _version = state.Version;
        _document = state.Document;
        var active = _session.IsActive || state.Phase is MeetingSessionPhase.Starting or MeetingSessionPhase.Recording or MeetingSessionPhase.Paused or MeetingSessionPhase.Finishing;
        var preparing = state.Phase == MeetingSessionPhase.Starting;
        var finishing = state.Phase == MeetingSessionPhase.Finishing;
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
            MeetingSessionPhase.Completed => "Completed",
            MeetingSessionPhase.Failed => "Stopped",
            _ => "Ready"
        };
        DelayText.Visibility = state.PendingSeconds >= 10 ? Visibility.Visible : Visibility.Collapsed;
        DelayText.Text = $"{FormatTime(state.PendingSeconds)} of captured audio awaiting transcription";
        SaveText.Text = "Text saves automatically";
        EmptyTitle.Text = preparing ? "Preparing local transcription" : active ? "Listening for your conversation" : "Ready when you are";
        EmptyDescription.Text = state.Phase == MeetingSessionPhase.Paused
            ? "Capture is paused. Resume to continue this meeting."
            : active ? "Text appears as each chunk is recognized. Speaker labels may be revised."
            : "Choose your meeting app below. Speaker-labelled text will appear here as you talk.";
        if (state.Error is not null) ShowError(state.Error);
        RenderDocument();
    }

    private void RenderDocument()
    {
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
        if (ReferenceEquals(_renderedTurns, _document.Turns) && ReferenceEquals(_renderedNames, _document.SpeakerNames)) return;
        var nearBottom = TranscriptScroll.ScrollableHeight - TranscriptScroll.VerticalOffset < 60;
        _renderedTurns = _document.Turns;
        _renderedNames = _document.SpeakerNames;
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
                Text = $"{FormatTime(turn.StartSeconds)}{(turn.IsProvisional ? " · provisional" : "")}",
                Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"]
            });
            block.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                Child = new TextBlock { Text = turn.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 16 }
            });
            TranscriptRows.Children.Add(block);
            previousSpeaker = turn.SpeakerId;
        }
        if (nearBottom) TranscriptScroll.UpdateLayout();
        if (nearBottom) TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null, true);
    }

    private async void StartClicked(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        var system = AudioSourceChoice.SelectedIndex == 1;
        var process = (ProcessChoice.SelectedItem as ComboBoxItem)?.Tag as MeetingProcessChoice;
        if (!system && process is null) throw new InvalidOperationException("Select a meeting app, or explicitly choose all system audio.");
        var title = MeetingTitle.Text;
        await _session.StartAsync(new MeetingCaptureRequest(system ? MeetingPlaybackSource.SystemAudio : MeetingPlaybackSource.MeetingApp, process?.Id));
        if (!string.IsNullOrWhiteSpace(title) && title != "New meeting") await _session.RenameAsync(title);
    });

    private async void PauseClicked(object sender, RoutedEventArgs args) => await RunActionAsync(() =>
        _session.State.Phase == MeetingSessionPhase.Paused ? _session.ResumeAsync() : _session.PauseAsync());
    private async void EndClicked(object sender, RoutedEventArgs args) => await RunActionAsync(() => _session.EndAsync());

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        ErrorBar.IsOpen = false;
        StartButton.IsEnabled = PauseButton.IsEnabled = EndButton.IsEnabled = false;
        try { await action(); }
        catch (Exception error) { ShowError(error.Message); }
        finally
        {
            _busy = false;
            if (!_savedMeeting) Render(_session.State);
            StartButton.IsEnabled = true;
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
        if (_document is null || (!_savedMeeting && _session.IsActive)) return;
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
        if (_allowClose || _savedMeeting) return;
        if (_busy) { args.Cancel = true; return; }
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

    private void AudioSourceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ProcessSelector is not null) ProcessSelector.Visibility = AudioSourceChoice.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RefreshProcessesClicked(object sender, RoutedEventArgs args) => RefreshProcesses();
    private void RefreshProcesses()
    {
        var choices = new List<MeetingProcessChoice>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                        choices.Add(new MeetingProcessChoice(process.Id, $"{process.ProcessName} · {process.MainWindowTitle}"));
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        ProcessChoice.Items.Clear();
        foreach (var choice in choices.OrderBy(p => p.DisplayName)) ProcessChoice.Items.Add(new ComboBoxItem { Content = choice.DisplayName, Tag = choice });
    }
    private void ShowError(string message) { ErrorBar.Title = "Meeting needs attention"; ErrorBar.Message = message; ErrorBar.IsOpen = true; }
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private sealed record MeetingProcessChoice(int Id, string DisplayName);
}
