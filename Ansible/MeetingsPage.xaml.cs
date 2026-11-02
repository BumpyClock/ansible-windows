using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Ansible;

public sealed partial class MeetingsPage : Page
{
    private readonly DictationSession _session;
    private readonly MeetingStore _store;
    private readonly Func<MeetingDocument?, Task> _openMeeting;
    private readonly Func<nint> _windowHandle;
    private CancellationTokenSource _shutdown = new();
    private Task? _exportTask;
    private Task? _refreshTask;
    private Task? _openTask;
    private IReadOnlyList<MeetingSummary> _displayedSummaries = [];
    private IReadOnlyList<MeetingReadFailure> _displayedFailures = [];
    private UiSessionObserver? _observer;

    internal MeetingsPage(DictationSession session, MeetingStore store, Func<MeetingDocument?, Task> openMeeting, Func<nint> windowHandle)
    {
        InitializeComponent();
        _session = session;
        _store = store;
        _openMeeting = openMeeting;
        _windowHandle = windowHandle;
        Loaded += async (_, _) =>
        {
            _observer = new UiSessionObserver(session, DispatcherQueue, RenderNotice);
            await RefreshAsync();
        };
        Unloaded += (_, _) => { _observer?.Dispose(); _observer = null; };
    }

    internal Task RefreshAsync()
    {
        if (_shutdown.IsCancellationRequested || _exportTask is { IsCompleted: false }) return Task.CompletedTask;
        if (_refreshTask is { IsCompleted: false }) return _refreshTask;
        return _refreshTask = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        try
        {
            var token = _shutdown.Token;
            var result = await Task.Run(() => _store.ListAsync(token), token);
            if (_shutdown.IsCancellationRequested) return;
            var summaries = result.Summaries;
            if (_displayedSummaries.SequenceEqual(summaries) && _displayedFailures.SequenceEqual(result.Failures)) return;
            _displayedSummaries = summaries;
            _displayedFailures = result.Failures;
            MeetingList.ItemsSource = summaries.Select(summary => new MeetingHistoryRow(summary)).ToArray();
            UnreadableMeetings.Children.Clear();
            foreach (var failure in result.Failures)
            {
                var row = new StackPanel { Spacing = 8, Padding = new Thickness(12) };
                row.Children.Add(new TextBlock { Text = "Saved transcript needs attention", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
                row.Children.Add(new TextBlock { Text = $"File: {failure.Id:N}.json", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"] });
                row.Children.Add(new TextBlock { Text = failure.Message, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = "Save the original transcript file somewhere safe, then remove it from Ansible. New meetings remain available.", TextWrapping = TextWrapping.Wrap });
                var export = new Button { Content = "Export and remove", Tag = failure.Id };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(export, $"Export and remove transcript {failure.Id:N}");
                export.Click += ExportUnreadableClicked;
                row.Children.Add(export);
                UnreadableMeetings.Children.Add(row);
            }
            UnreadableMeetings.Visibility = result.Failures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Visibility = summaries.Count == 0 && result.Failures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MeetingList.Visibility = summaries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
    }

    private async void ExportUnreadableClicked(object sender, RoutedEventArgs args)
    {
        if (_shutdown.IsCancellationRequested || _exportTask is { IsCompleted: false } || sender is not Button { Tag: Guid id }) return;
        _exportTask = ExportUnreadableAsync(id);
        await _exportTask;
    }

    private async Task ExportUnreadableAsync(Guid id)
    {
        var buttons = UnreadableMeetings.Children.OfType<StackPanel>().SelectMany(row => row.Children.OfType<Button>()).ToArray();
        foreach (var button in buttons) button.IsEnabled = false;
        try
        {
            if (_refreshTask is not null) await _refreshTask;
            _shutdown.Token.ThrowIfCancellationRequested();
            var picker = new FileSavePicker { SuggestedFileName = $"Meeting-{id:N}" };
            picker.FileTypeChoices.Add("Original transcript file", [".json"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
            var file = await picker.PickSaveFileAsync().AsTask(_shutdown.Token);
            if (file is null) return;
            await _store.ExportAndRemoveAsync(id, file.Path, _shutdown.Token);
            await RefreshCoreAsync();
            NewMeetingButton.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
        finally { foreach (var button in buttons) button.IsEnabled = true; }
    }

    internal async Task BeginShutdownAsync()
    {
        _shutdown.Cancel();
        if (_exportTask is not null) await _exportTask;
        if (_refreshTask is not null) await _refreshTask;
        if (_openTask is not null) await _openTask;
    }

    internal void ResumeAfterFailedShutdown()
    {
        _shutdown.Dispose();
        _shutdown = new CancellationTokenSource();
    }

    private async void NewMeetingClicked(object sender, RoutedEventArgs args)
    {
        if (_shutdown.IsCancellationRequested || _openTask is { IsCompleted: false }) return;
        _openTask = OpenAsync(null);
        await _openTask;
    }

    private async void MeetingClicked(object sender, ItemClickEventArgs args)
    {
        if (_shutdown.IsCancellationRequested || _openTask is { IsCompleted: false } ||
            args.ClickedItem is not MeetingHistoryRow row) return;
        _openTask = OpenAsync(row.Summary.Id);
        await _openTask;
    }

    private async Task OpenAsync(Guid? id)
    {
        try
        {
            var token = _shutdown.Token;
            var document = id is { } savedId ? await Task.Run(() => _store.LoadAsync(savedId, token), token) : null;
            token.ThrowIfCancellationRequested();
            await _openMeeting(document);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowError(Exception error) =>
        _session.Notify(NoticeKind.Error, "Meeting unavailable", error.Message, NoticeArea.Meetings);

    private void RenderNotice(SessionSnapshot state)
    {
        var notice = state.Notice;
        ErrorBar.Severity = notice.Kind == NoticeKind.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        ErrorBar.Title = notice.Title;
        ErrorBar.Message = notice.Message;
        ErrorBar.IsOpen = notice.ShouldDisplayOn(NoticeArea.Meetings);
        ErrorBar.Visibility = ErrorBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ErrorBarClosing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton) _session.DismissNotice(_session.State.Notice);
        sender.Visibility = Visibility.Collapsed;
    }
}

public sealed record MeetingHistoryRow(MeetingSummary Summary)
{
    public string Title => Summary.Title;
    public string Description => $"{Summary.CreatedAt.ToLocalTime():MMM d, yyyy \u00b7 h:mm tt} \u00b7 {Summary.Status}";
}
