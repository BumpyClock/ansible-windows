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
    // A strip card is never narrower than this: the 256 DIP activity clock plus card padding and border. The strip
    // drops a column and wraps to a new row first.
    private const double StripCardMinWidth = 300;

    private HomeInsightsViewModel? _model;

    public HomeInsightsView()
    {
        InitializeComponent();
    }

    /// <summary>False stacks the cards as a side rail. True flows them into columns as a strip above history.</summary>
    internal bool Strip
    {
        get => RailCards.MinColumnWidth < double.PositiveInfinity;
        set => RailCards.MinColumnWidth = value ? StripCardMinWidth : double.PositiveInfinity;
    }

    internal void Render(HomeInsightsViewModel model)
    {
        _model = model;
        HomePaceText.Text = model.PaceText;
        HomeSessionsText.Text = model.SessionsText;
        HomeWordsText.Text = model.WordsText;
        HomeStreakPill.Text = model.StreakText ?? "";
        HomeStreakPill.Visibility = model.StreakText is null ? Visibility.Collapsed : Visibility.Visible;
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
            ShowActivityMessage(_model.AvailabilityMessage ?? "Activity statistics are not available yet.");
            return;
        }

        var activity = HomeInsightsProjection.CreateActivity(_model.HourlyActivity, _model.Today,
            ActivityPeriodSelector.SelectedItem != WeekPeriodItem);
        ActivityPeriodText.Text = activity.PeriodText;
        if (activity.IsEmpty)
        {
            ShowActivityMessage("No dictations in this period.");
            return;
        }
        ActivityEmptyPanel.Visibility = Visibility.Collapsed;
        ActivityChartPanel.Visibility = Visibility.Visible;
        RhythmCard.VerticalAlignment = VerticalAlignment.Top;
        ActivityTotalText.Text = activity.TotalValueText;
        ActivityUnitText.Text = activity.UnitText;
        AutomationProperties.SetName(ActivityTotalText, activity.TotalText);
        AutomationProperties.SetHelpText(ActivityTotalText,
            $"{activity.PeriodText}. {string.Join(". ", activity.Hours.Select(hour => hour.Description))}");
        ActivityScaleText.Text = $"Busiest hour · {activity.ScaleText}";
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
            // Every tick is short: the hour labels sit just outside the ring at their own size, not on long ticks.
            ticks.Children.Add(new LineGeometry { StartPoint = ClockPoint(angle, 129), EndPoint = ClockPoint(angle, 133) });
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

    // One calm message replaces the chart, its scale, dates, and footnote. In a strip the card then fills its row
    // beside the usage card and centres the message, instead of leaving a short card beside a tall one.
    private void ShowActivityMessage(string message)
    {
        HomeActivityAvailabilityText.Text = message;
        ActivityEmptyPanel.Visibility = Visibility.Visible;
        ActivityChartPanel.Visibility = Visibility.Collapsed;
        RhythmCard.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private static Point ClockPoint(double angle, double radius) =>
        new(160 + Math.Cos(angle) * radius, 160 + Math.Sin(angle) * radius);
}
