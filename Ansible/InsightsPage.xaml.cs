using System.Globalization;
using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Ansible;

public sealed partial class InsightsPage : Page
{
    private readonly DictationSession _session;
    private SessionSnapshot _state;
    private UiSessionObserver? _observer;
    private UsageDocument? _usage;
    private IReadOnlyList<AudioModel>? _models;
    private bool _collectUsage;
    private UsageSummary? _summary;
    private double _cellSize = 14;

    internal InsightsPage(DictationSession session)
    {
        InitializeComponent();
        _session = session;
        _state = session.State;
        Loaded += (_, _) =>
        {
            _usage = null;
            _models = null;
            _observer = new UiSessionObserver(session, DispatcherQueue, state =>
            {
                _state = state;
                if (!ReferenceEquals(_usage, state.Usage) || !ReferenceEquals(_models, state.Models) ||
                    _collectUsage != state.Settings.CollectUsage || state.UsageError is not null)
                {
                    _usage = state.Usage;
                    _models = state.Models;
                    _collectUsage = state.Settings.CollectUsage;
                    RenderUsage();
                }
            });
        };
        Unloaded += (_, _) => { _observer?.Dispose(); _observer = null; };
    }

    private void RenderUsage()
    {
        UsageErrorBar.IsOpen = _state.UsageError is not null;
        UsageErrorBar.Visibility = _state.UsageError is null ? Visibility.Collapsed : Visibility.Visible;
        UsageErrorBar.Title = "Usage statistics unavailable";
        UsageErrorBar.Message = _state.UsageError ?? "";
        if (_state.Usage is null) { return; }
        var summary = UsageSummary.Create(
            _state.Usage.Entries, DateOnly.FromDateTime(DateTime.Today), TimeZoneInfo.Local);
        IntroText.Text = _state.Settings.CollectUsage
            ? ""
            : "Usage collection is paused. Saved counts remain visible.";
        IntroText.Visibility = IntroText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PaceText.Text = summary.WordsPerMinute?.ToString("N0", CultureInfo.CurrentCulture) ?? "--";
        ToolTipService.SetToolTip(PaceText,
            "Recognized dictation words divided by recorded microphone time, including pauses." +
            (summary.RecordingSeconds > 0 ? $" {summary.RecordingSeconds / 60:N1} min recorded." : ""));
        SessionsText.Text = summary.DictationSessions.ToString("N0");
        ToolTipService.SetToolTip(SessionsText, "Only completed sessions count." +
            (summary.FileSessions > 0
                ? $" {summary.FileSessions:N0} {(summary.FileSessions == 1 ? "file" : "files")} transcribed."
                : ""));
        TotalWordsText.Text = summary.TotalWords.ToString("N0");
        ToolTipService.SetToolTip(TotalWordsText, (summary.UnknownWordSessions > 0
            ? $"Excludes {summary.UnknownWordSessions:N0} " +
              $"{(summary.UnknownWordSessions == 1 ? "session" : "sessions")} with unknown word counts."
            : "Excludes sessions with unknown word counts.") +
            (summary.FileWords > 0 ? $" {summary.FileWords:N0} from files." : ""));
        StreakText.Text = $"{summary.CurrentStreak} day streak";
        ToolTipService.SetToolTip(StreakText, summary.LongestStreak > summary.CurrentStreak
            ? $"Best {summary.LongestStreak} {(summary.LongestStreak == 1 ? "day" : "days")}."
            : "Consecutive days with a completed session.");
        RenderModels(summary);
        _summary = summary;
        RenderActivity(summary);
    }

    private static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    private void RenderModels(UsageSummary summary)
    {
        ModelRows.Children.Clear();
        foreach (var model in summary.Models.Take(5))
        {
            var label = _state.Models.FirstOrDefault(candidate => candidate.Id == model.ModelId)?.DisplayName ?? model.ModelId;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = label, Style = AppStyle("BodyTextBlockStyle") };
            var count = new TextBlock
            {
                Text = $"{model.Sessions:N0}", Style = AppStyle("BodyTextBlockStyle"), Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(count, 1);
            row.Children.Add(name);
            row.Children.Add(count);
            ModelRows.Children.Add(row);
        }
        if (summary.Models.Count == 0)
        {
            ModelRows.Children.Add(new TextBlock
            {
                Text = "No completed sessions yet.",
                TextWrapping = TextWrapping.Wrap,
                Style = AppStyle("SecondaryTextStyle")
            });
        }
    }

    private void RenderActivity(UsageSummary summary)
    {
        const int weeks = 13;
        const double labelColumn = 16;
        var cellSize = _cellSize;
        HeatmapGrid.Children.Clear();
        HeatmapGrid.ColumnDefinitions.Clear();
        HeatmapGrid.RowDefinitions.Clear();
        HeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelColumn) });
        for (var column = 0; column < weeks; column++)
        {
            HeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        HeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var row = 0; row < 7; row++)
        {
            HeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellSize) });
        }
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-84);
        start = start.AddDays(-(int)start.DayOfWeek);
        // Rows start on Sunday, so Monday, Wednesday, and Friday are rows 1, 3, and 5.
        foreach (var (row, text) in new[] { (1, "M"), (3, "W"), (5, "F") })
        {
            var label = new TextBlock
            {
                Text = text, Style = AppStyle("CaptionSecondaryStyle"), VerticalAlignment = VerticalAlignment.Center,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = cellSize
            };
            Grid.SetRow(label, row + 1);
            HeatmapGrid.Children.Add(label);
        }
        var monthColumns = Enumerable.Range(0, weeks)
            .Where(column => column == 0 || start.AddDays(column * 7).Month != start.AddDays(column * 7 - 7).Month)
            .ToList();
        // Drop the first label when the next month begins too soon for both labels to fit.
        if (monthColumns.Count > 1 && monthColumns[1] < 3) { monthColumns.RemoveAt(0); }
        for (var index = 0; index < monthColumns.Count; index++)
        {
            var column = monthColumns[index];
            var month = new TextBlock
            {
                Text = start.AddDays(column * 7).ToString("MMM", CultureInfo.CurrentCulture),
                Style = AppStyle("CaptionTextBlockStyle"), TextWrapping = TextWrapping.NoWrap
            };
            Grid.SetColumn(month, column + 1);
            Grid.SetColumnSpan(month, (index + 1 < monthColumns.Count ? monthColumns[index + 1] : weeks) - column);
            HeatmapGrid.Children.Add(month);
        }
        for (var column = 0; column < weeks; column++)
        {
            for (var row = 0; row < 7; row++)
            {
                var day = start.AddDays(column * 7 + row);
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
                    Style = AppStyle(styleKey), Width = cellSize, Height = cellSize, CornerRadius = new CornerRadius(3)
                };
                var description = $"{day.ToString("ddd d MMM", CultureInfo.CurrentCulture)}: " +
                    $"{count} {(count == 1 ? "session" : "sessions")}";
                ToolTipService.SetToolTip(cell, description);
                AutomationProperties.SetName(cell, description);
                Grid.SetRow(cell, row + 1);
                Grid.SetColumn(cell, column + 1);
                HeatmapGrid.Children.Add(cell);
            }
        }
    }

    private void HeatmapHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        // 13 week columns plus the label column are separated by 13 gaps of 4 DIPs.
        var cell = Math.Clamp(Math.Floor((args.NewSize.Width - 16 - 13 * 4) / 13), 12, 20);
        if (cell == _cellSize) { return; }
        _cellSize = cell;
        if (_summary is not null) { RenderActivity(_summary); }
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
            for (var index = 0; index < 3; index++) { SummaryGrid.ColumnDefinitions.Add(new ColumnDefinition()); }
            SummaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DetailGrid.ColumnDefinitions.Add(new ColumnDefinition());
            DetailGrid.ColumnDefinitions.Add(new ColumnDefinition());
            DetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        Grid.SetColumn(SessionsCard, narrow ? 0 : 1);
        Grid.SetRow(SessionsCard, narrow ? 1 : 0);
        Grid.SetColumn(WordsCard, narrow ? 0 : 2);
        Grid.SetRow(WordsCard, narrow ? 2 : 0);
        Grid.SetColumn(ActivityCard, narrow ? 0 : 1);
        Grid.SetRow(ActivityCard, narrow ? 1 : 0);
    }
}
