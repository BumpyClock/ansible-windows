using Ansible.Core;
using System.Globalization;

namespace Ansible;

public sealed record MeetingDayHeader(string Label);

/// <summary>One saved meeting on the Meetings page. The row sits under its day header, so it names the time, not the date.</summary>
public sealed record MeetingHistoryRow(MeetingSummary Summary)
{
    private DateTime Local => Summary.CreatedAt.LocalDateTime;
    private bool Untitled => MeetingText.IsUntitled(Summary.Title);

    public string Title => Untitled ? $"Meeting at {MeetingText.Time(Local)}" : Summary.Title;

    /// <summary>Time (unless the title already says it), length, and speakers. Never empty, so every row has two lines.</summary>
    public string Details => string.Join(" · ", new[]
    {
        Untitled ? null : MeetingText.Time(Local),
        Summary.Duration is { } duration ? MeetingText.Duration(duration) : "No speech transcribed",
        MeetingText.Speakers(Summary.SpeakerCount)
    }.OfType<string>());

    public bool IsFailed => Summary.Status == MeetingStatus.Failed;
    public string Status => MeetingText.Status(Summary.Status) ?? "";
    public bool HasNeutralStatus => !IsFailed && MeetingText.Status(Summary.Status) is not null;
    public string AccessibleName => $"{Title}, {Details}{(Status.Length == 0 ? "" : $", {Status}")}";
}

internal static class MeetingList
{
    /// <summary>Newest first, with a day header before each local day's meetings.</summary>
    public static List<object> Create(IEnumerable<MeetingSummary> summaries, DateOnly today)
    {
        var items = new List<object>();
        DateOnly? day = null;
        foreach (var summary in summaries.OrderByDescending(summary => summary.CreatedAt))
        {
            var date = DateOnly.FromDateTime(summary.CreatedAt.LocalDateTime);
            if (date != day) items.Add(new MeetingDayHeader(DayLabel.For(date, today)));
            day = date;
            items.Add(new MeetingHistoryRow(summary));
        }
        return items;
    }
}

/// <summary>Plain-language meeting text shared by the Meetings list and the meeting window.</summary>
internal static class MeetingText
{
    // Every meeting is created with this title. It names nothing, so views show when the meeting happened instead.
    private const string PlaceholderTitle = "New meeting";

    public static bool IsUntitled(string title) => string.Equals(title.Trim(), PlaceholderTitle, StringComparison.Ordinal);

    /// <summary>The user's or Teams' title, or the meeting's local start date and time.</summary>
    public static string Title(string title, DateTimeOffset createdAt, DateOnly today) =>
        IsUntitled(title) ? $"Meeting on {Date(createdAt.LocalDateTime, today)}, {Time(createdAt.LocalDateTime)}" : title;

    public static string Time(DateTime local) => local.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>Weekday and abbreviated date in the culture's month and day order, with the year only outside this year.</summary>
    public static string Date(DateTime local, DateOnly today)
    {
        var culture = CultureInfo.CurrentCulture;
        if (local.Year != today.Year) return local.ToString("d", culture);
        var monthDay = culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal);
        return $"{local.ToString("ddd", culture)}, {local.ToString(monthDay, culture)}";
    }

    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1)) return "Under 1 min";
        var minutes = (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero);
        var (hours, rest) = (minutes / 60, minutes % 60);
        return hours == 0 ? $"{rest} min" : rest == 0 ? $"{hours} hr" : $"{hours} hr {rest} min";
    }

    public static string? Speakers(int count) => count switch
    {
        <= 0 => null,
        1 => "1 speaker",
        _ => $"{count} speakers"
    };

    /// <summary>A saved meeting's state, or null when it completed normally and needs no label.</summary>
    public static string? Status(MeetingStatus status) => status switch
    {
        MeetingStatus.Completed => null,
        MeetingStatus.Failed => "Failed",
        MeetingStatus.Interrupted => "Interrupted",
        MeetingStatus.Starting => "Starting",
        MeetingStatus.Recording => "Recording",
        MeetingStatus.Paused => "Paused",
        MeetingStatus.Finishing => "Finishing",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    /// <summary>The saved meeting window's one detail line: date unless the title says it, length, speakers, and state.</summary>
    public static string SavedDetails(MeetingDocument document, DateOnly today)
    {
        var local = document.CreatedAt.LocalDateTime;
        return string.Join(" · ", new[]
        {
            IsUntitled(document.Title) ? null : $"{Date(local, today)}, {Time(local)}",
            MeetingSummary.SpeechDuration(document) is { } duration ? Duration(duration) : null,
            Speakers(MeetingSummary.SpeakerCountOf(document)),
            Status(document.Status)
        }.OfType<string>());
    }
}
