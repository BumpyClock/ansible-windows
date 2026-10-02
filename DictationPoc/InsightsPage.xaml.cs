using System.Globalization;
using DictationPoc.Core;
using DictationPoc.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DictationPoc;

public sealed partial class InsightsPage : Page
{
    private readonly DictationController _controller;

    public InsightsPage()
    {
        InitializeComponent();
        _controller = ((App)Application.Current).Controller;
        Loaded += (_, _) =>
        {
            _controller.Changed += RenderState;
            _controller.UsageChanged += RenderUsage;
            RenderState();
            RenderUsage();
        };
        Unloaded += (_, _) =>
        {
            _controller.Changed -= RenderState;
            _controller.UsageChanged -= RenderUsage;
        };
    }

    private void RenderState() => StartButton.IsEnabled = _controller.CanStart;

    private void RenderUsage()
    {
        UsageErrorBar.IsOpen = _controller.UsageError is not null;
        UsageErrorBar.Visibility = _controller.UsageError is null ? Visibility.Collapsed : Visibility.Visible;
        UsageErrorBar.Title = "Usage statistics unavailable";
        UsageErrorBar.Message = _controller.UsageError ?? "";
        if (_controller.Usage is null) { return; }
        var summary = UsageSummary.Create(
            _controller.Usage.Entries, DateOnly.FromDateTime(DateTime.Today), TimeZoneInfo.Local);
        IntroText.Text = _controller.Usage.Enabled
            ? "Your activity, measured on this device. No audio or transcript history is saved."
            : "Local usage collection is paused. Previously saved counts remain visible.";
        PaceText.Text = summary.WordsPerMinute?.ToString("N0", CultureInfo.CurrentCulture) ?? "--";
        PaceCaption.Text = summary.RecordingSeconds > 0 ? $"{summary.RecordingSeconds / 60:N1} recorded min" : "No dictations yet";
        SessionsText.Text = summary.Sessions.ToString("N0");
        DictationsText.Text = $"{summary.DictationSessions:N0} dictations";
        VerificationsText.Text = $"{summary.FileSessions:N0} file verifications";
        TotalWordsText.Text = summary.TotalWords.ToString("N0");
        WordsCaption.Text = summary.TotalWords == 0 ? "Start a dictation to see your voice add up." : "A little more of your day, written in your own voice.";
        WordsSplitText.Text = $"{summary.DictatedWords:N0} dictated / {summary.FileWords:N0} verified";
        WordsShareBar.Visibility = summary.TotalWords == 0 ? Visibility.Collapsed : Visibility.Visible;
        DictationShare.Width = new GridLength(summary.DictatedWords, GridUnitType.Star);
        FileShare.Width = new GridLength(summary.FileWords, GridUnitType.Star);
        StreakText.Text = $"{summary.CurrentStreak} day streak";
        LongestText.Text = $"BEST: {summary.LongestStreak} DAYS";
        RenderPace(summary.WordsPerMinute);
        RenderModels(summary);
        RenderActivity(summary);
    }

    private void RenderPace(double? pace)
    {
        if (pace is null || pace <= 0)
        {
            PaceArc.Data = null;
            return;
        }
        var ratio = Math.Clamp(pace.Value / 200, 0.001, 1);
        var angle = Math.PI * (1 - ratio);
        var figure = new PathFigure { StartPoint = new Point(12, 78) };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(80 + 68 * Math.Cos(angle), 78 - 68 * Math.Sin(angle)),
            Size = new Size(68, 68), SweepDirection = SweepDirection.Clockwise
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        PaceArc.Data = geometry;
        ToolTipService.SetToolTip(PaceText, "Recognized dictation words divided by recorded microphone time, including pauses. Visual scale: 0 to 200 words per minute.");
    }

    private void RenderModels(UsageSummary summary)
    {
        ModelRows.Children.Clear();
        ModelsCaption.Text = summary.Models.Count == 0
            ? "No completed sessions yet. Try dictation or verify a WAV file."
            : $"{summary.Models.Count} models used / {summary.Sessions:N0} completed sessions";
        foreach (var model in summary.Models.Take(5))
        {
            var label = _controller.Models.FirstOrDefault(candidate => candidate.Id == model.ModelId)?.DisplayName ?? model.ModelId;
            var stack = new StackPanel { Spacing = 7 };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            var count = new TextBlock { Text = $"{model.Sessions:N0}", FontSize = 13, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(count, 1);
            row.Children.Add(name);
            row.Children.Add(count);
            var bar = new ProgressBar
            {
                Minimum = 0, Maximum = summary.Sessions, Value = model.Sessions,
                Foreground = (Brush)Application.Current.Resources["AccentBrush"],
                Background = (Brush)Application.Current.Resources["InactiveChartBrush"],
                Height = 8
            };
            AutomationProperties.SetName(bar, $"{label}: {model.Sessions} sessions, {model.Words} recognized words");
            stack.Children.Add(row);
            stack.Children.Add(bar);
            ModelRows.Children.Add(stack);
        }
        if (summary.Models.Count == 0)
        {
            ModelRows.Children.Add(new TextBlock
            {
                Text = "Your model comparisons will appear here.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"],
                Margin = new Thickness(0, 32, 0, 32)
            });
        }
    }

    private void RenderActivity(UsageSummary summary)
    {
        HeatmapGrid.Children.Clear();
        HeatmapGrid.ColumnDefinitions.Clear();
        HeatmapGrid.RowDefinitions.Clear();
        HeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
        for (var column = 0; column < 13; column++)
        {
            HeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var row = 0; row < 8; row++)
        {
            HeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        }
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-84);
        start = start.AddDays(-(int)start.DayOfWeek);
        var days = new[] { "S", "M", "T", "W", "T", "F", "S" };
        for (var row = 0; row < 7; row++)
        {
            var label = new TextBlock { Text = days[row], FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row + 1);
            HeatmapGrid.Children.Add(label);
        }
        for (var column = 0; column < 13; column++)
        {
            var date = start.AddDays(column * 7);
            if (column == 0 || date.Month != date.AddDays(-7).Month)
            {
                var month = new TextBlock { Text = date.ToString("MMM", CultureInfo.CurrentCulture), FontSize = 10 };
                Grid.SetColumn(month, column + 1);
                HeatmapGrid.Children.Add(month);
            }
            for (var row = 0; row < 7; row++)
            {
                var day = date.AddDays(row);
                var count = summary.Activity.GetValueOrDefault(day);
                var color = count switch
                {
                    0 => Color.FromArgb(255, 209, 206, 196),
                    1 => Color.FromArgb(255, 205, 235, 229),
                    2 => Color.FromArgb(255, 121, 193, 183),
                    _ => Color.FromArgb(255, 24, 92, 94)
                };
                var cell = new Border
                {
                    Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(2), Opacity = day > today ? 0.35 : 1
                };
                ToolTipService.SetToolTip(cell, $"{day:MMM d, yyyy}: {count} completed sessions");
                Grid.SetRow(cell, row + 1);
                Grid.SetColumn(cell, column + 1);
                HeatmapGrid.Children.Add(cell);
            }
        }
        ActivityCaption.Text = summary.Activity.Count == 0
            ? "No activity recorded yet. Only completed sessions fill the calendar."
            : $"Active on {summary.Activity.Count:N0} days. The calendar shows recent local sessions.";
    }

    private void LayoutChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < 720;
        if (SummaryGrid.ColumnDefinitions.Count == (narrow ? 1 : 3)) { return; }
        SummaryGrid.ColumnDefinitions.Clear();
        SummaryGrid.RowDefinitions.Clear();
        DetailGrid.ColumnDefinitions.Clear();
        DetailGrid.RowDefinitions.Clear();
        if (narrow)
        {
            SummaryGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var index = 0; index < 3; index++) { SummaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            DetailGrid.ColumnDefinitions.Add(new ColumnDefinition());
            DetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        else
        {
            SummaryGrid.ColumnDefinitions.Add(new ColumnDefinition());
            SummaryGrid.ColumnDefinitions.Add(new ColumnDefinition());
            SummaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            SummaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DetailGrid.ColumnDefinitions.Add(new ColumnDefinition());
            DetailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            DetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        Grid.SetColumn(SessionsCard, narrow ? 0 : 1);
        Grid.SetRow(SessionsCard, narrow ? 1 : 0);
        Grid.SetColumn(WordsCard, narrow ? 0 : 2);
        Grid.SetRow(WordsCard, narrow ? 2 : 0);
        Grid.SetColumn(ActivityCard, narrow ? 0 : 1);
        Grid.SetRow(ActivityCard, narrow ? 1 : 0);
    }

    private async void StartClicked(object sender, RoutedEventArgs args) => await _controller.StartDictationAsync();
    private void VerifyClicked(object sender, RoutedEventArgs args) => ((App)Application.Current).Window!.Navigate("settings");
}
