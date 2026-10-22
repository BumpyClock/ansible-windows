using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

internal sealed partial class HomeModelTemplateSelector : DataTemplateSelector
{
    public DataTemplate PrimaryTemplate { get; set; } = null!;
    public DataTemplate SecondaryTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) =>
        item is HomeModelViewModel { IsPrimary: true } ? PrimaryTemplate : SecondaryTemplate;
}

public sealed partial class HomeInsightsView : UserControl
{
    private double _cellSize;
    private IReadOnlyDictionary<DateOnly, int>? _activity;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);

    public HomeInsightsView()
    {
        InitializeComponent();
    }

    internal void Render(HomeInsightsViewModel model)
    {
        UsageWarningBar.Message = model.UsageWarning ?? "";
        UsageWarningBar.IsOpen = model.UsageWarning is not null;
        UsageWarningBar.Visibility = UsageWarningBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        HomePaceText.Text = model.PaceText;
        HomeSessionsText.Text = model.SessionsText;
        HomeWordsText.Text = model.WordsText;
        HomeStreakText.Text = model.StreakText;
        HomeInsightsFootnote.Text = model.Footnote;
        HomeModelRows.ItemsSource = model.Models.Cast<object>().ToList();
        NoModelDataText.Text = model.AvailabilityMessage ?? "No completed sessions yet.";
        NoModelDataText.Visibility = model.Models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _activity = model.Activity;
        _today = model.Today;
        var activityAvailable = model.Activity is not null;
        HomeActivityAvailabilityText.Text = model.AvailabilityMessage ?? "Activity statistics are not available yet.";
        HomeActivityAvailabilityText.Visibility = activityAvailable ? Visibility.Collapsed : Visibility.Visible;
        HomeHeatmapHost.Visibility = activityAvailable ? Visibility.Visible : Visibility.Collapsed;
        HomeActivityLegend.Visibility = activityAvailable ? Visibility.Visible : Visibility.Collapsed;
        HomeStreakPill.Visibility = activityAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (activityAvailable)
        {
            RenderActivity(model.Activity!, model.Today);
        }
        else
        {
            ClearActivity();
        }
    }

    internal void SetWideLayout(bool wide)
    {
        if (wide) { HomeActivityCard.MinHeight = (double)Application.Current.Resources["HomeActivityCardMinHeight"]; }
        else { HomeActivityCard.ClearValue(FrameworkElement.MinHeightProperty); }
    }

    private void ClearActivity()
    {
        HomeHeatmapGrid.Children.Clear();
        HomeHeatmapGrid.ColumnDefinitions.Clear();
        HomeHeatmapGrid.RowDefinitions.Clear();
    }

    private void RenderActivity(IReadOnlyDictionary<DateOnly, int> activity, DateOnly today)
    {
        const int weeks = 13;
        const double labelColumn = 16;
        var gap = (double)Application.Current.Resources["HomeActivityHeatmapGap"];
        var cellSize = _cellSize;
        if (cellSize <= 0)
        {
            cellSize = (double)Application.Current.Resources["HomeLegendChipSize"];
            if (HomeHeatmapHost.ActualWidth > 0)
                cellSize = Math.Clamp(Math.Floor((HomeHeatmapHost.ActualWidth - 16 - 13 * gap) / 13), 12, 20);
        }
        var cellCornerRadius = (CornerRadius)Application.Current.Resources["HomeLegendChipCornerRadius"];
        ClearActivity();
        HomeHeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelColumn) });
        for (var column = 0; column < weeks; column++) HomeHeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        HomeHeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var row = 0; row < 7; row++) HomeHeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellSize) });
        var start = today.AddDays(-84);
        start = start.AddDays(-(int)start.DayOfWeek);
        foreach (var (row, text) in new[] { (1, "M"), (3, "W"), (5, "F") })
        {
            var label = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"],
                VerticalAlignment = VerticalAlignment.Center, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = cellSize };
            Grid.SetRow(label, row + 1);
            HomeHeatmapGrid.Children.Add(label);
        }
        var monthColumns = Enumerable.Range(0, weeks).Where(column => column == 0 || start.AddDays(column * 7).Month != start.AddDays(column * 7 - 7).Month).ToList();
        if (monthColumns.Count > 1 && monthColumns[1] < 3) monthColumns.RemoveAt(0);
        for (var index = 0; index < monthColumns.Count; index++)
        {
            var column = monthColumns[index];
            var month = new TextBlock { Text = start.AddDays(column * 7).ToString("MMM", System.Globalization.CultureInfo.CurrentCulture),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextWrapping = TextWrapping.NoWrap };
            Grid.SetColumn(month, column + 1);
            Grid.SetColumnSpan(month, (index + 1 < monthColumns.Count ? monthColumns[index + 1] : weeks) - column);
            HomeHeatmapGrid.Children.Add(month);
        }
        for (var column = 0; column < weeks; column++)
            for (var row = 0; row < 7; row++)
            {
                var day = start.AddDays(column * 7 + row);
                var count = activity.GetValueOrDefault(day);
                var styleKey = day > today ? "ActivityFutureCellStyle" : count switch
                {
                    0 => "ActivityEmptyCellStyle", 1 => "ActivityLowCellStyle", 2 => "ActivityMediumCellStyle", _ => "ActivityHighCellStyle"
                };
                var cell = new Border { Style = (Style)Application.Current.Resources[styleKey], Width = cellSize, Height = cellSize, CornerRadius = cellCornerRadius };
                var unit = count == 1 ? "session" : "sessions";
                var description = $"{day:ddd d MMM}: {count} {unit}";
                if (day > today) description = $"{day:ddd d MMM}: Not yet recorded";
                ToolTipService.SetToolTip(cell, description);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, description);
                Grid.SetRow(cell, row + 1);
                Grid.SetColumn(cell, column + 1);
                HomeHeatmapGrid.Children.Add(cell);
            }
    }

    private void HomeHeatmapHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var gap = (double)Application.Current.Resources["HomeActivityHeatmapGap"];
        var cell = Math.Clamp(Math.Floor((args.NewSize.Width - 16 - 13 * gap) / 13), 12, 20);
        if (cell == _cellSize) { return; }
        _cellSize = cell;
        if (_activity is not null) { RenderActivity(_activity, _today); }
    }
}
