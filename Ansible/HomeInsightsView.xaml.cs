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
    // A strip card narrower than this wraps to the next row, like the mockup's auto-fit minmax(220px, 1fr).
    private const double StripCardMinWidth = 220;

    private HomeInsightsViewModel? _model;
    private bool _strip;
    private int _columns;

    public HomeInsightsView()
    {
        InitializeComponent();
        ArrangeCards(1);
    }

    /// <summary>False stacks the cards as a side rail. True flows them into columns as a strip above history.</summary>
    internal bool Strip
    {
        get => _strip;
        set
        {
            if (_strip == value) return;
            _strip = value;
            ArrangeCards(ColumnsFor(RailCards.ActualWidth));
        }
    }

    private int ColumnsFor(double width)
    {
        if (!_strip || width <= 0) return 1;
        var gap = RailCards.ColumnSpacing;
        return Math.Clamp((int)((width + gap) / (StripCardMinWidth + gap)), 1, RailCards.Children.Count);
    }

    private void RailCardsSizeChanged(object sender, SizeChangedEventArgs args) => ArrangeCards(ColumnsFor(args.NewSize.Width));

    private void ArrangeCards(int columns)
    {
        if (columns == _columns) return;
        _columns = columns;
        var cards = RailCards.Children;
        RailCards.ColumnDefinitions.Clear();
        RailCards.RowDefinitions.Clear();
        for (var column = 0; column < columns; column++)
            RailCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < (cards.Count + columns - 1) / columns; row++)
            RailCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < cards.Count; index++)
        {
            Grid.SetColumn((FrameworkElement)cards[index], index % columns);
            Grid.SetRow((FrameworkElement)cards[index], index / columns);
        }
    }

    internal void Render(HomeInsightsViewModel model)
    {
        _model = model;
        HomePaceText.Text = model.PaceText;
        HomeSessionsText.Text = model.SessionsText;
        HomeWordsText.Text = model.WordsText;
        HomeStreakPill.Text = model.StreakText;
        HomeStreakPill.Visibility = model.Activity is null ? Visibility.Collapsed : Visibility.Visible;
        HomeMicrophoneText.Text = model.MicrophoneMinutesText;
        ToolTipService.SetToolTip(HomePaceText, $"{model.PaceText} words per microphone minute, pauses included");
        ToolTipService.SetToolTip(HomeSessionsText, $"{model.SessionsText} dictations, all time");
        ToolTipService.SetToolTip(HomeMicrophoneText, $"{model.MicrophoneMinutesText} microphone minutes, pauses included");
        AutomationProperties.SetName(HomePaceText, $"{model.PaceText} words per microphone minute, all time, pauses included");
        AutomationProperties.SetName(HomeSessionsText, $"{model.SessionsText} dictations, all time");
        AutomationProperties.SetName(HomeMicrophoneText, $"{model.MicrophoneMinutesText} microphone minutes, all time, pauses included");
        AutomationProperties.SetName(HomeWordsText, $"{model.WordsText} words dictated, all time");
        HomeInsightsFootnote.Text = model.Footnote;
        HomeModelRows.ItemsSource = model.Models.Cast<object>().ToList();
        NoModelDataText.Text = model.AvailabilityMessage ?? "No completed sessions yet.";
        NoModelDataText.Visibility = model.Models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderActivity();
    }

    private void ActivityPeriodChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_model is not null) RenderActivity();
    }

    private void RenderActivity()
    {
        if (_model is null) return;
        ClockHitTargets.Children.Clear();
        ActivityPeriodSelector.IsEnabled = _model.HourlyActivity is not null;
        if (_model.HourlyActivity is null)
        {
            ActivityTotalText.Text = "--";
            HomeActivityAvailabilityText.Text = _model.AvailabilityMessage ?? "Activity statistics are not available yet.";
            HomeActivityAvailabilityText.Visibility = Visibility.Visible;
            ActivityChartPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var activity = HomeInsightsProjection.CreateActivity(_model.HourlyActivity, _model.Today,
            ActivityPeriodSelector.SelectedItem != WeekPeriodItem);
        ActivityPeriodText.Text = activity.PeriodText;
        ActivityTotalText.Text = activity.TotalValueText;
        ActivityUnitText.Text = activity.UnitText;
        AutomationProperties.SetName(ActivityTotalText, activity.TotalText);
        AutomationProperties.SetHelpText(ActivityTotalText,
            $"{activity.PeriodText}. {string.Join(". ", activity.Hours.Select(hour => hour.Description))}");
        ActivityScaleText.Text = $"Busiest hour · {activity.ScaleText}";
        ActivityStartText.Text = activity.StartText;
        ActivityEndText.Text = activity.EndText;
        HomeActivityAvailabilityText.Text = "No dictations in this period.";
        HomeActivityAvailabilityText.Visibility = activity.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        ActivityChartPanel.Visibility = Visibility.Visible;
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

    private static Point ClockPoint(double angle, double radius) =>
        new(160 + Math.Cos(angle) * radius, 160 + Math.Sin(angle) * radius);
}
