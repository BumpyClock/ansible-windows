using Ansible.Core;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Ansible;

public sealed partial class MeetingsPage : Page
{
    private readonly DictationSession _session;
    private readonly MeetingStore _store;
    private readonly LocalDiagnosticLog _diagnostics;
    private readonly Func<MeetingDocument?, Task> _openMeeting;
    private readonly Func<nint> _windowHandle;
    private CancellationTokenSource _shutdown = new();
    private Task? _exportTask;
    private Task? _refreshTask;
    private Task? _openTask;
    private IReadOnlyList<MeetingSummary> _displayedSummaries = [];
    private IReadOnlyList<MeetingReadFailure> _displayedFailures = [];
    private readonly List<Button> _exportButtons = [];
    private bool _rendered;

    internal MeetingsPage(DictationSession session, MeetingStore store, Func<MeetingDocument?, Task> openMeeting,
        Func<nint> windowHandle, LocalDiagnosticLog diagnostics, AppNotices notices)
    {
        InitializeComponent();
        _session = session;
        _store = store;
        _diagnostics = diagnostics;
        _openMeeting = openMeeting;
        _windowHandle = windowHandle;
        Loaded += async (_, _) => await RefreshAsync();
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
            if (_rendered && _displayedSummaries.SequenceEqual(summaries) && _displayedFailures.SequenceEqual(result.Failures)) return;
            _rendered = true;
            _displayedSummaries = summaries;
            _displayedFailures = result.Failures;
            // WinRT rejects arrays of managed records as ItemsSource; a List<object> marshals as a bindable vector.
            MeetingGroups.ItemsSource = MeetingList.Create(summaries, DateOnly.FromDateTime(DateTime.Today));
            UnreadableMeetings.Children.Clear();
            _exportButtons.Clear();
            foreach (var failure in result.Failures)
            {
                var row = new StackPanel { Spacing = 8 };
                row.Children.Add(new TextBlock { Text = "Saved transcript needs attention", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
                row.Children.Add(new TextBlock { Text = $"File: {failure.Id:N}.json", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"] });
                row.Children.Add(new TextBlock { Text = failure.Message, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = "Save the original transcript file somewhere safe, then remove it from Ansible. New meetings remain available.", TextWrapping = TextWrapping.Wrap });
                var export = new Button { Content = "Export and remove", Tag = failure.Id };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(export, $"Export and remove transcript {failure.Id:N}");
                export.Click += ExportUnreadableClicked;
                row.Children.Add(export);
                _exportButtons.Add(export);
                UnreadableMeetings.Children.Add(new Border
                {
                    Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(16), Child = row
                });
            }
            UnreadableMeetings.Visibility = result.Failures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Visibility = summaries.Count == 0 && result.Failures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MeetingGroups.Visibility = summaries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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
        var buttons = _exportButtons.ToArray();
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

    private async void MeetingClicked(object sender, RoutedEventArgs args)
    {
        if (_shutdown.IsCancellationRequested || _openTask is { IsCompleted: false } ||
            sender is not Button { Tag: Guid id }) return;
        _openTask = OpenAsync(id);
        await _openTask;
    }

    private async Task OpenAsync(Guid? id)
    {
        var generation = id is null ? null : _diagnostics.CurrentGeneration;
        var started = generation is null ? 0 : Stopwatch.GetTimestamp();
        Exception? loadFailure = null;
        try
        {
            var token = _shutdown.Token;
            MeetingDocument? document;
            try { document = id is { } savedId ? await Task.Run(() => _store.LoadAsync(savedId, token), token) : null; }
            catch (Exception error) { loadFailure = error; throw; }
            finally
            {
                if (generation is { } value)
                    _diagnostics.Write(new StorageDiagnostic(DateTimeOffset.UtcNow, StorageDiagnosticCategory.Meeting,
                        StorageDiagnosticStage.Load,
                        loadFailure is OperationCanceledException ? DiagnosticOutcome.Cancelled :
                        loadFailure is null ? DiagnosticOutcome.Completed : DiagnosticOutcome.Failed,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, loadFailure?.HResult), value);
            }
            token.ThrowIfCancellationRequested();
            await _openMeeting(document);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowError(Exception error) =>
        _session.Notify(NoticeKind.Error, "Meeting unavailable", error.Message, NoticeArea.Meetings);

}

internal sealed partial class MeetingListTemplateSelector : DataTemplateSelector
{
    public DataTemplate DayTemplate { get; set; } = null!;
    public DataTemplate RowTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) => item is MeetingDayHeader ? DayTemplate : RowTemplate;

    // ItemsControl asks with its container; the base overload does not forward to the one above.
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

public sealed record MeetingDayHeader(string Label);

internal static class MeetingList
{
    /// <summary>Newest first, with a day header before each local day's meetings.</summary>
    public static List<object> Create(IEnumerable<MeetingSummary> summaries, DateOnly today)
    {
        var items = new List<object>();
        DateOnly? day = null;
        foreach (var summary in summaries.OrderByDescending(summary => summary.CreatedAt))
        {
            var date = DateOnly.FromDateTime(summary.CreatedAt.LocalDateTime);
            if (date != day) items.Add(new MeetingDayHeader(DayLabel(date, today)));
            day = date;
            items.Add(new MeetingHistoryRow(summary));
        }
        return items;
    }

    // Same labels as the Home history. The year appears only for dates outside the current year.
    private static string DayLabel(DateOnly date, DateOnly today)
    {
        if (date == today) return "TODAY";
        if (date == today.AddDays(-1)) return "YESTERDAY";
        var format = date.Year == today.Year ? "ddd, MMM d" : "ddd, MMM d, yyyy";
        return date.ToString(format, CultureInfo.CurrentCulture).ToUpperInvariant();
    }
}

public sealed record MeetingHistoryRow(MeetingSummary Summary)
{
    public string Title => Summary.Title;
    public string Time => Summary.CreatedAt.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
    public Visibility FailedVisibility => Summary.Status == MeetingStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
}
