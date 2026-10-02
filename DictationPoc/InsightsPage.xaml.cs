using System.Globalization;
using DictationPoc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DictationPoc;

public sealed partial class InsightsPage : Page
{
    private readonly DictationSession _session;
    private readonly Action<string> _navigate;
    private SessionSnapshot _state;
    private UiSessionObserver? _observer;
    private UsageDocument? _usage;
    private IReadOnlyList<AudioModel>? _models;

    internal InsightsPage(DictationSession session, Action<string> navigate)
    {
        InitializeComponent();
        _session = session;
        _navigate = navigate;
        _state = session.State;
        Loaded += (_, _) =>
        {
            _usage = null;
            _models = null;
            _observer = new UiSessionObserver(session, DispatcherQueue, state =>
            {
                _state = state;
                RenderState();
                if (!ReferenceEquals(_usage, state.Usage) || !ReferenceEquals(_models, state.Models) || state.UsageError is not null)
                {
                    _usage = state.Usage;
                    _models = state.Models;
                    RenderUsage();
                }
            });
        };
        Unloaded += (_, _) => { _observer?.Dispose(); _observer = null; };
    }

    private void RenderState() => StartButton.IsEnabled = _state.CanStart;

    private void RenderUsage()
    {
        UsageErrorBar.IsOpen = _state.UsageError is not null;
        UsageErrorBar.Visibility = _state.UsageError is null ? Visibility.Collapsed : Visibility.Visible;
        UsageErrorBar.Title = "Usage statistics unavailable";
        UsageErrorBar.Message = _state.UsageError ?? "";
        if (_state.Usage is null) { return; }
        var summary = UsageSummary.Create(
            _state.Usage.Entries, DateOnly.FromDateTime(DateTime.Today), TimeZoneInfo.Local);
        IntroText.Text = _state.Usage.Enabled
            ? "Your activity, measured on this device. No audio or transcript history is saved."
            : "Local usage collection is paused. Previously saved counts remain visible.";
        if (summary.UnknownWordSessions > 0)
        {
            IntroText.Text += $" Word totals exclude {summary.UnknownWordSessions} sessions without authoritative speech content.";
        }
        PaceText.Text = summary.WordsPerMinute?.ToString("N0", CultureInfo.CurrentCulture) ?? "--";
        PaceCaption.Text = summary.RecordingSeconds > 0 ? $"{summary.RecordingSeconds / 60:N1} measured min" :
            summary.DictationSessions > 0 ? "Awaiting speech counts" : "No dictations yet";
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
            var label = _state.Models.FirstOrDefault(candidate => candidate.Id == model.ModelId)?.DisplayName ?? model.ModelId;
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
                Style = (Style)Application.Current.Resources["ModelUsageProgressStyle"]
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
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
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
                var styleKey = day > today ? "ActivityFutureCellStyle" : count switch
                {
                    0 => "ActivityEmptyCellStyle",
                    1 => "ActivityLowCellStyle",
                    2 => "ActivityMediumCellStyle",
                    _ => "ActivityHighCellStyle"
                };
                var cell = new Border
                {
                    Style = (Style)Application.Current.Resources[styleKey], CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(2)
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

    private async void StartClicked(object sender, RoutedEventArgs args)
    {
        try { await _session.StartDictationAsync(); }
        catch (Exception error) { _session.ReportUiError(error); }
    }
    private void VerifyClicked(object sender, RoutedEventArgs args) => _navigate("settings");
}
