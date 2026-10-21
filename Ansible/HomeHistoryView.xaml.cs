using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ansible;

public sealed partial class HomeHistoryView : UserControl
{
    internal event Action? QueryChanged;
    internal event Action<HomeHistoryItem>? CopyRequested;

    internal string Query => HistorySearchBox.Text.Trim();

    public HomeHistoryView()
    {
        InitializeComponent();
    }

    internal void Render(HomeHistoryViewModel model)
    {
        HistoryCount.Text = $"{model.TodayCount:N0} {(model.TodayCount == 1 ? "dictation" : "dictations")} today";
        OversizedNotice.Text = $"A transcript over {HomeDictationHistory.MaximumCharacters:N0} characters stays in the current result and is not added to session history.";
        OversizedNotice.Visibility = model.OversizedCurrentResult ? Visibility.Visible : Visibility.Collapsed;
        HistoryItems.ItemsSource = model.Groups.Cast<object>().ToList();
        HistoryItems.Visibility = model.HasMatches ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = model.HasMatches ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = model.HasHistory ? "No matching transcripts." : "No completed dictations in this app session.";
        EmptyDetail.Text = model.HasHistory ? "Try a different search." : "Choose Dictate to start your history.";
        HistoryRows.VerticalAlignment = model.HasMatches ? VerticalAlignment.Stretch : VerticalAlignment.Center;
    }

    private void HistorySearchChanged(object sender, TextChangedEventArgs args) => QueryChanged?.Invoke();

    private void HistoryCopyClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: HomeHistoryItem item }) { CopyRequested?.Invoke(item); }
    }

    private void FocusHistorySearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        HistorySearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void HistoryHeaderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < (double)Application.Current.Resources["HistorySearchStackThreshold"];
        HistoryHeaderGrid.ColumnDefinitions.Clear();
        HistoryHeaderGrid.RowDefinitions.Clear();
        if (narrow)
        {
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HistoryHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HistoryHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(HistoryTitle, 0);
            Grid.SetColumn(HistoryCountPill, 1);
            Grid.SetColumn(HistorySearchBox, 0); Grid.SetColumnSpan(HistorySearchBox, 2); Grid.SetRow(HistorySearchBox, 1);
            Grid.SetColumn(HistorySearchIcon, 0); Grid.SetColumnSpan(HistorySearchIcon, 2); Grid.SetRow(HistorySearchIcon, 1);
            Grid.SetColumn(HistorySearchShortcut, 0); Grid.SetColumnSpan(HistorySearchShortcut, 2); Grid.SetRow(HistorySearchShortcut, 1);
            HistorySearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            HistoryHeaderGrid.RowSpacing = 10;
        }
        else
        {
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)Application.Current.Resources["HistorySearchColumnWidth"] });
            HistoryHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(HistoryTitle, 0); Grid.SetColumn(HistoryCountPill, 1);
            Grid.SetColumn(HistorySearchBox, 3); Grid.SetColumnSpan(HistorySearchBox, 1); Grid.SetRow(HistorySearchBox, 0);
            Grid.SetColumn(HistorySearchIcon, 3); Grid.SetColumnSpan(HistorySearchIcon, 1); Grid.SetRow(HistorySearchIcon, 0);
            Grid.SetColumn(HistorySearchShortcut, 3); Grid.SetColumnSpan(HistorySearchShortcut, 1); Grid.SetRow(HistorySearchShortcut, 0);
            HistorySearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            HistoryHeaderGrid.RowSpacing = 0;
        }
        HistorySearchBox.Margin = new Thickness(0);
    }
}
