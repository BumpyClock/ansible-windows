using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class MeetingsPage : Page
{
    private readonly MeetingStore _store;
    private readonly Func<MeetingDocument?, Task> _openMeeting;

    internal MeetingsPage(MeetingStore store, Func<MeetingDocument?, Task> openMeeting)
    {
        InitializeComponent();
        _store = store;
        _openMeeting = openMeeting;
        Loaded += async (_, _) => await RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        try
        {
            var documents = await _store.ListAsync();
            MeetingList.Items.Clear();
            foreach (var document in documents)
            {
                var row = new StackPanel { Spacing = 6, Padding = new Thickness(12, 16, 12, 16) };
                row.Children.Add(new TextBlock { Text = document.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
                row.Children.Add(new TextBlock
                {
                    Text = $"{document.CreatedAt.ToLocalTime():MMM d, yyyy · h:mm tt} · {document.Status}",
                    Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"]
                });
                MeetingList.Items.Add(new ListViewItem { Content = row, Tag = document, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            EmptyState.Visibility = documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MeetingList.Visibility = documents.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void NewMeetingClicked(object sender, RoutedEventArgs args)
    {
        try { await _openMeeting(null); }
        catch (Exception error) { ShowError(error); }
    }

    private async void MeetingClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not ListViewItem { Tag: MeetingDocument document }) return;
        try { await _openMeeting(await _store.LoadAsync(document.Id)); }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowError(Exception error)
    {
        ErrorBar.Title = "Meeting unavailable";
        ErrorBar.Message = error.Message;
        ErrorBar.IsOpen = true;
    }
}
