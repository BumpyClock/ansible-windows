using Ansible.Core;
using System.Globalization;

namespace Ansible;

public sealed record MeetingDayHeader(string Label);

public sealed record MeetingHistoryRow(MeetingSummary Summary)
{
    public string Title => MeetingText.Title(Summary);
    public string Details => MeetingText.ListDetails(Summary);
    public bool IsEmpty => MeetingList.IsEmpty(Summary);
    public bool IsFailed => Summary.Status == MeetingStatus.Failed;
    public bool IsCriticalFailure => IsFailed && Summary.Duration is not null;
    public string Status => IsEmpty ? "" : MeetingText.Status(Summary.Status) ?? "";
    public string AccessibleName => $"{Title}, {Details}{(Status.Length == 0 ? "" : $", {Status}")}";
    public string OptionsName => $"More options for {Title}";
}

internal static class MeetingList
{
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

    public static bool IsFinished(MeetingStatus status) =>
        status is MeetingStatus.Completed or MeetingStatus.Failed or MeetingStatus.Interrupted;

    public static IReadOnlyList<Guid> Empty(IEnumerable<MeetingSummary> summaries) =>
        summaries.Where(IsEmpty).Select(summary => summary.Id).ToArray();

    public static bool IsEmpty(MeetingSummary summary) => summary.Duration is null && IsFinished(summary.Status);
}

internal static class MeetingText
{
    public static string Title(MeetingSummary summary) =>
        summary.IsUntitled ? $"Meeting at {Time(summary.CreatedAt.LocalDateTime)}" : summary.Title;

    public static string Time(DateTime local) => local.ToString("t", CultureInfo.CurrentCulture);

    public static string Date(DateTime local, DateOnly today) => DayLabel.Date(DateOnly.FromDateTime(local), today);

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

    public static string? Outcome(MeetingStatus status, bool hasSpeech) => (status, hasSpeech) switch
    {
        (MeetingStatus.Completed, false) => "No speech transcribed",
        (MeetingStatus.Failed, false) => "Failed before any speech was transcribed",
        (MeetingStatus.Failed, true) => "Text before the failure is saved",
        (MeetingStatus.Interrupted, false) => "Interrupted before any speech was transcribed",
        (MeetingStatus.Interrupted, true) => "Text before the interruption is saved",
        (_, false) => "No speech transcribed yet",
        _ => null
    };

    public static string EmptyExplanation(MeetingStatus status) => status switch
    {
        MeetingStatus.Failed => "This meeting failed before any speech was transcribed. The cause isn't saved with the meeting.",
        MeetingStatus.Interrupted => "This meeting was interrupted before any speech was transcribed.",
        MeetingStatus.Completed => "Nothing was recognized in this meeting.",
        _ => "Speech appears here once it is transcribed."
    };

    public static string ListDetails(MeetingSummary summary) => Details(summary, null,
        Outcome(summary.Status, summary.Duration is not null),
        summary.IsUntitled ? null : Time(summary.CreatedAt.LocalDateTime));

    public static string SavedDetails(MeetingSummary summary, DateOnly today)
    {
        var local = summary.CreatedAt.LocalDateTime;
        return Details(summary, summary.IsUntitled ? Date(local, today) : $"{Date(local, today)}, {Time(local)}",
            Status(summary.Status));
    }

    private static string Details(MeetingSummary summary, string? lead, params string?[] trail) => string.Join(" · ", new[]
    {
        lead,
        summary.Duration is { } duration ? Duration(duration) : null,
        summary.Duration is null ? null : Speakers(summary.SpeakerCount)
    }.Concat(trail).OfType<string>());

    public static (string Title, string Content) DeleteConfirmation(int count) => count == 1
        ? ("Delete this meeting?", "The saved transcript will be permanently deleted.")
        : ($"Delete {count} empty meetings?", "None of them has any transcribed speech. They will be permanently deleted.");
}
