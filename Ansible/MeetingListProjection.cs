using Ansible.Core;
using System.Globalization;

namespace Ansible;

public sealed record MeetingDayHeader(string Label);

public sealed record MeetingHistoryRow(MeetingSummary Summary)
{
    public string Title => Summary.Title;
    public string Time => Summary.CreatedAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
    public bool IsFailed => Summary.Status == MeetingStatus.Failed;
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
