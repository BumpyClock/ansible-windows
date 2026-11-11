using Ansible.Core;
using System.Globalization;

namespace Ansible;

public sealed record MeetingDayHeader(string Label);

/// <summary>One saved meeting on the Meetings page. The row sits under its day header, so it names the time, not the date.</summary>
public sealed record MeetingHistoryRow(MeetingSummary Summary)
{
    private DateTime Local => Summary.CreatedAt.LocalDateTime;
    private bool Untitled => MeetingText.IsUntitled(Summary.Title);
    private bool HasSpeech => Summary.Duration is not null;

    public string Title => MeetingText.Title(Summary.Title, Summary.CreatedAt);

    /// <summary>
    /// Length and speakers first, so rows differ at a glance; then what is known about how the meeting ended; then the
    /// start time when the title does not already say it. Never empty, so every row has two lines.
    /// </summary>
    public string Details => string.Join(" · ", new[]
    {
        HasSpeech ? MeetingText.Duration(Summary.Duration!.Value) : null,
        HasSpeech ? MeetingText.Speakers(Summary.SpeakerCount) : null,
        MeetingText.Outcome(Summary.Status, HasSpeech),
        Untitled ? null : MeetingText.Time(Local)
    }.OfType<string>());

    /// <summary>A finished meeting with nothing to read. Its title recedes so meetings with transcripts stand out.</summary>
    public bool IsEmpty => MeetingList.IsEmpty(Summary);
    public bool HasReadableTitle => !IsEmpty;
    public bool IsFailed => Summary.Status == MeetingStatus.Failed;
    /// <summary>A failure that interrupted a meeting with saved text. Only this failure badge uses the critical colour.</summary>
    public bool IsCriticalFailure => IsFailed && HasSpeech;
    /// <summary>
    /// The badge text. Empty for a finished meeting with no speech, whose detail line already names the state
    /// (Failed or Interrupted), so a row never states it twice.
    /// </summary>
    public string Status => IsEmpty ? "" : MeetingText.Status(Summary.Status) ?? "";
    public bool HasNeutralStatus => !IsFailed && Status.Length != 0;
    /// <summary>A meeting still being captured belongs to its session; only a finished one can be deleted from the list.</summary>
    public bool CanDelete => MeetingList.IsFinished(Summary.Status);
    public string AccessibleName => $"{Title}, {Details}{(Status.Length == 0 ? "" : $", {Status}")}";
    public string OptionsName => $"More options for {Title}";
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

    public static bool IsFinished(MeetingStatus status) =>
        status is MeetingStatus.Completed or MeetingStatus.Failed or MeetingStatus.Interrupted;

    /// <summary>
    /// Finished meetings with no transcribed speech: what one confirmation may delete together. A meeting that holds
    /// any transcript text is never in this set, whatever its status.
    /// </summary>
    public static IReadOnlyList<Guid> Empty(IEnumerable<MeetingSummary> summaries) =>
        summaries.Where(IsEmpty).Select(summary => summary.Id).ToArray();

    public static bool IsEmpty(MeetingSummary summary) => summary.Duration is null && IsFinished(summary.Status);
}

/// <summary>Plain-language meeting text shared by the Meetings list and the meeting window.</summary>
internal static class MeetingText
{
    // Every meeting is created with this title. It names nothing, so views show when the meeting happened instead.
    private const string PlaceholderTitle = "New meeting";

    public static bool IsUntitled(string title) => string.Equals(title.Trim(), PlaceholderTitle, StringComparison.Ordinal);

    /// <summary>The user's or Teams' title unchanged, or the meeting's local start time. The list row and the window use this one name.</summary>
    public static string Title(string title, DateTimeOffset createdAt) =>
        IsUntitled(title) ? $"Meeting at {Time(createdAt.LocalDateTime)}" : title;

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

    /// <summary>
    /// What is known about how a meeting ended. A saved meeting records only its status, never a cause: Failed covers a
    /// recognition, capture, or storage error and a start that was cancelled; Interrupted covers discarded pending
    /// speech and a meeting the app closed during. Null when the length and speakers say enough.
    /// </summary>
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

    /// <summary>The saved meeting window's explanation when it has no transcript to show.</summary>
    public static string EmptyExplanation(MeetingStatus status) => status switch
    {
        MeetingStatus.Failed => "This meeting failed before any speech was transcribed. The cause isn't saved with the meeting.",
        MeetingStatus.Interrupted => "This meeting was interrupted before any speech was transcribed.",
        MeetingStatus.Completed => "Nothing was recognized in this meeting.",
        _ => "Speech appears here once it is transcribed."
    };

    /// <summary>The saved meeting window's one detail line: date (and time, unless the title says it), length, speakers, and state.</summary>
    public static string SavedDetails(MeetingDocument document, DateOnly today)
    {
        var local = document.CreatedAt.LocalDateTime;
        return string.Join(" · ", new[]
        {
            IsUntitled(document.Title) ? Date(local, today) : $"{Date(local, today)}, {Time(local)}",
            MeetingSummary.SpeechDuration(document) is { } duration ? Duration(duration) : null,
            Speakers(MeetingSummary.SpeakerCountOf(document)),
            Status(document.Status)
        }.OfType<string>());
    }

    /// <summary>
    /// The one confirmation before saved meetings are permanently deleted. One meeting is any meeting the user chose;
    /// several are always the <see cref="MeetingList.Empty"/> set.
    /// </summary>
    public static (string Title, string Content) DeleteConfirmation(int count) => count == 1
        ? ("Delete this meeting?", "The saved transcript will be permanently deleted.")
        : ($"Delete {count} empty meetings?", "None of them has any transcribed speech. They will be permanently deleted.");
}
