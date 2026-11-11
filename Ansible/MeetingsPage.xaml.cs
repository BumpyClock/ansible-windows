using Ansible.Core;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;

namespace Ansible;

public sealed partial class MeetingsPage : Page
{
    private const string ErrorNoticeKey = "meetings-error";
    private const string DeleteNoticeKey = "meetings-delete";

    private readonly AppNotices _notices;
    private readonly MeetingStore _store;
    private readonly LocalDiagnosticLog _diagnostics;
    private readonly Func<MeetingDocument?, Task> _openMeeting;
    private readonly Func<nint> _windowHandle;
    private CancellationTokenSource _shutdown = new();
    private Task? _exportTask;
    private Task? _refreshTask;
    private Task? _openTask;
    private Task? _deleteTask;
    private ContentDialog? _deleteDialog;
    private IReadOnlyList<MeetingSummary> _displayedSummaries = [];
    private IReadOnlyList<MeetingReadFailure> _displayedFailures = [];
    private readonly List<Button> _exportButtons = [];
    private bool _rendered;
    private DateOnly _displayedDate;

    internal MeetingsPage(MeetingStore store, Func<MeetingDocument?, Task> openMeeting,
        Func<nint> windowHandle, LocalDiagnosticLog diagnostics, AppNotices notices)
    {
        InitializeComponent();
        var canvas = (PageCanvas)Content;
        canvas.PrimaryAction = new PageAction(NewMeetingButton, NewMeetingClicked, NewMeetingLabel);
        canvas.PrimaryActionIcon = new FontIconSource { Glyph = "", FontSize = 16 };
        _notices = notices;
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
            _notices.Set(ErrorNoticeKey, null);
            var summaries = result.Summaries;
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (_rendered && _displayedDate == today && _displayedSummaries.SequenceEqual(summaries) &&
                _displayedFailures.SequenceEqual(result.Failures)) return;
            _rendered = true;
            _displayedDate = today;
            _displayedSummaries = summaries;
            _displayedFailures = result.Failures;
            // WinRT rejects arrays of managed records as ItemsSource; a List<object> marshals as a bindable vector.
            MeetingGroups.ItemsSource = MeetingList.Create(summaries, today);
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
                AutomationProperties.SetName(export, $"Export and remove transcript {failure.Id:N}");
                export.Click += ExportUnreadableClicked;
                row.Children.Add(export);
                _exportButtons.Add(export);
                UnreadableMeetings.Children.Add(new Border
                {
                    Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(16), Child = row
                });
            }
            UnreadableMeetings.Visibility = result.Failures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            NoMeetings.Visibility = summaries.Count == 0 && result.Failures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MeetingGroups.Visibility = summaries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var empty = MeetingList.Empty(summaries).Count;
            MoreOptionsButton.Visibility = empty >= 2 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(DeleteEmptyMenuItem, $"Delete {empty} empty meetings");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
    }

    private async void ExportUnreadableClicked(object sender, RoutedEventArgs args)
    {
        if (_shutdown.IsCancellationRequested || _exportTask is { IsCompleted: false } || _deleteTask is { IsCompleted: false } ||
            sender is not Button { Tag: Guid id }) return;
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
        _deleteDialog?.Hide();
        if (_deleteTask is not null) await _deleteTask;
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

    private void MeetingClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: Guid id }) Open(id);
    }

    private async void Open(Guid id)
    {
        if (_shutdown.IsCancellationRequested || _openTask is { IsCompleted: false }) return;
        _openTask = OpenAsync(id);
        await _openTask;
    }

    private void MeetingOptionsClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: Guid id } button ||
            _displayedSummaries.FirstOrDefault(candidate => candidate.Id == id) is not { } summary) return;
        var open = new MenuFlyoutItem { Text = "Open", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => Open(id);
        var delete = new MenuFlyoutItem
        {
            Text = "Delete…", Icon = new FontIcon { Glyph = "" }, IsEnabled = new MeetingHistoryRow(summary).CanDelete
        };
        delete.Click += (_, _) => Delete([id]);
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(open);
        menu.Items.Add(delete);
        menu.ShowAt(button);
    }

    private void DeleteEmptyClicked(object sender, RoutedEventArgs args) => Delete(MeetingList.Empty(_displayedSummaries));

    private async void Delete(IReadOnlyList<Guid> ids)
    {
        if (_shutdown.IsCancellationRequested || ids.Count == 0 || _deleteTask is { IsCompleted: false } ||
            _exportTask is { IsCompleted: false }) return;
        _deleteTask = DeleteAsync(ids);
        await _deleteTask;
    }

    private async Task DeleteAsync(IReadOnlyList<Guid> ids)
    {
        var deleted = false;
        try
        {
            _deleteDialog = MeetingDeletion.Dialog(ids.Count, XamlRoot);
            if (await _deleteDialog.ShowAsync() != ContentDialogResult.Primary) return;
            _notices.Set(DeleteNoticeKey, null);
            var token = _shutdown.Token;
            foreach (var id in ids)
            {
                await _store.DeleteAsync(id, token);
                deleted = true;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            _notices.Set(DeleteNoticeKey, new NoticeContent(NoticeSeverity.Error,
                deleted ? "Some meetings were not deleted" : "Couldn't delete the meeting", error.Message), NoticeArea.Meetings);
        }
        finally { _deleteDialog = null; }
        if (!deleted || _shutdown.IsCancellationRequested) return;
        if (_refreshTask is not null) await _refreshTask;
        await (_refreshTask = RefreshCoreAsync());
        NewMeetingButton.Focus(FocusState.Programmatic);
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
            _notices.Set(ErrorNoticeKey, null);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowError(Exception error) => _notices.Set(ErrorNoticeKey,
        new NoticeContent(NoticeSeverity.Error, "Meeting unavailable", error.Message), NoticeArea.Meetings);

    public static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

internal sealed partial class MeetingListTemplateSelector : DataTemplateSelector
{
    public DataTemplate DayTemplate { get; set; } = null!;
    public DataTemplate RowTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) => item is MeetingDayHeader ? DayTemplate : RowTemplate;

    // ItemsControl asks with its container; the base overload does not forward to the one above.
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
