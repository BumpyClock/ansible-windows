using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

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
    private const double StripCardMinWidth = 300;

    private HomeUsageStats? _stats;
    private DateOnly _today;

    public HomeInsightsView()
    {
        InitializeComponent();
    }

    internal bool Strip
    {
        get => RailCards.MinColumnWidth < double.PositiveInfinity;
        set
        {
            RailCards.MinColumnWidth = value ? StripCardMinWidth : double.PositiveInfinity;
            UpdateCardHeights();
        }
    }

    private void UpdateCardHeights()
    {
        WordsCard.VerticalAlignment = Strip ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        var fill = Strip || ActivityEmptyState.Visibility == Visibility.Visible;
        RhythmCard.VerticalAlignment = fill ? VerticalAlignment.Stretch : VerticalAlignment.Top;
    }

    internal void Render(HomeInsightsViewModel model)
    {
        _today = model.Today;
        switch (model.Usage)
        {
            case HomeUsageStats stats:
                _stats = stats;
                ShowTotals(stats.PaceText, stats.SessionsText, stats.WordsText, stats.MicrophoneMinutesText,
                    stats.StreakText, stats.Footnote);
                ShowModels(stats.Models, "No completed sessions yet.");
                RenderActivity(stats);
                break;
            case HomeUsageUnavailable unavailable:
                _stats = null;
                var unknown = HomeInsightsProjection.UnknownNumber;
                ShowTotals(unknown, unknown, unknown, unknown, null, unavailable.Message);
                ShowModels([], unavailable.Message);
                ClockHitTargets.Children.Clear();
                ActivityPeriodSelector.IsEnabled = false;
                ShowActivityMessage("No activity to show", unavailable.Message);
                break;
        }
    }

    private void ShowTotals(string pace, string sessions, string words, string microphoneMinutes, string? streak, string footnote)
    {
        HomePaceText.Text = pace;
        HomeSessionsText.Text = sessions;
        HomeWordsText.Text = words;
        HomeStreakPill.Text = streak ?? "";
        HomeStreakPill.Visibility = streak is null ? Visibility.Collapsed : Visibility.Visible;
        HomeMicrophoneText.Text = microphoneMinutes;
        ToolTipService.SetToolTip(HomePaceText, $"{pace} words per microphone minute, pauses included");
        ToolTipService.SetToolTip(HomeSessionsText, $"{sessions} dictations, all time");
        ToolTipService.SetToolTip(HomeMicrophoneText, $"{microphoneMinutes} microphone minutes, pauses included");
        AutomationProperties.SetName(HomePaceText, $"{pace} words per microphone minute, all time, pauses included");
        AutomationProperties.SetName(HomeSessionsText, $"{sessions} dictations, all time");
        AutomationProperties.SetName(HomeMicrophoneText, $"{microphoneMinutes} microphone minutes, all time, pauses included");
        AutomationProperties.SetName(HomeWordsText, $"{words} words dictated, all time");
        HomeInsightsFootnote.Text = footnote;
    }

    private void ShowModels(IReadOnlyList<HomeModelViewModel> models, string emptyText)
    {
        HomeModelRows.ItemsSource = models.Cast<object>().ToList();
        NoModelDataText.Text = emptyText;
        NoModelDataText.Visibility = models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ActivityPeriodChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_stats is not null) RenderActivity(_stats);
    }

    private void RenderActivity(HomeUsageStats stats)
    {
        ClockHitTargets.Children.Clear();
        ActivityPeriodSelector.IsEnabled = true;
        var activity = HomeInsightsProjection.CreateActivity(stats.HourlyActivity, _today,
            month: ActivityPeriodSelector.SelectedItem != WeekPeriodItem);
        ActivityPeriodText.Text = activity.PeriodText;
        ActivityEmptyState.Visibility = Visibility.Collapsed;
        ActivityChartPanel.Visibility = Visibility.Visible;
        UpdateCardHeights();
        ActivityTotalText.Text = activity.TotalValueText;
        ActivityUnitText.Text = activity.UnitText;
        AutomationProperties.SetName(ActivityTotalText, activity.TotalText);
        AutomationProperties.SetHelpText(ActivityTotalText,
            $"{activity.PeriodText}. {string.Join(". ", activity.Hours.Select(hour => hour.Description))}");
        ActivityScaleText.Text = activity.IsEmpty
            ? stats.LastDictationText ?? "No dictations in this period"
            : $"Busiest hour · {activity.ScaleText}";
        ActivityStartText.Text = activity.StartText;
        ActivityEndText.Text = activity.EndText;
        AutomationProperties.SetName(ActivityClock,
            $"{activity.TotalText}. {activity.PeriodText}. Clockwise from midnight. Each sector is one local completion hour.");
        var background = new GeometryGroup();
        var active = new GeometryGroup();
        foreach (var dot in HomeInsightsProjection.CreateClockDots(activity.Hours))
        {
            var geometry = new EllipseGeometry { Center = new Point(dot.X, dot.Y), RadiusX = 1.5, RadiusY = 1.5 };
            (dot.IsActive ? active : background).Children.Add(geometry);
        }
        ClockBackgroundDots.Data = background;
        ClockActiveDots.Data = active;
        var ticks = new GeometryGroup();
        foreach (var hour in activity.Hours)
        {
            var angle = hour.Hour * Math.Tau / 24 - Math.PI / 2;
            ticks.Children.Add(new LineGeometry
            {
                StartPoint = ClockPoint(angle, 129),
                EndPoint = ClockPoint(angle, hour.Hour % 6 == 0 ? 138 : 133)
            });
            var end = angle + Math.Tau / 24;
            var sector = new PathFigure { StartPoint = ClockPoint(angle, 70), IsClosed = true, IsFilled = true };
            sector.Segments.Add(new LineSegment { Point = ClockPoint(angle, 127) });
            sector.Segments.Add(new ArcSegment { Point = ClockPoint(end, 127), Size = new Size(127, 127), SweepDirection = SweepDirection.Clockwise });
            sector.Segments.Add(new LineSegment { Point = ClockPoint(end, 70) });
            sector.Segments.Add(new ArcSegment { Point = ClockPoint(angle, 70), Size = new Size(70, 70), SweepDirection = SweepDirection.Counterclockwise });
            var shape = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = new PathGeometry { Figures = { sector } },
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            ToolTipService.SetToolTip(shape, hour.Description);
            AutomationProperties.SetName(shape, hour.Description);
            AutomationProperties.SetAccessibilityView(shape, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
            ClockHitTargets.Children.Add(shape);
        }
        ClockTicks.Data = ticks;
    }

    private void ShowActivityMessage(string title, string? detail)
    {
        ActivityEmptyState.Title = title;
        ActivityEmptyState.Detail = detail ?? "";
        ActivityEmptyState.Visibility = Visibility.Visible;
        ActivityChartPanel.Visibility = Visibility.Collapsed;
        UpdateCardHeights();
    }

    private static Point ClockPoint(double angle, double radius) =>
        new(160 + Math.Cos(angle) * radius, 160 + Math.Sin(angle) * radius);
}
