using System.Globalization;

namespace Ansible;

/// <summary>The day headings shared by Home history and Meetings.</summary>
internal static class DayLabel
{
    /// <summary>Today, Yesterday, then the date in sentence case. The year appears only for dates outside the current year.</summary>
    public static string For(DateOnly date, DateOnly today)
    {
        if (date == today) return "Today";
        if (date == today.AddDays(-1)) return "Yesterday";
        var format = date.Year == today.Year ? "ddd, MMM d" : "ddd, MMM d, yyyy";
        return date.ToString(format, CultureInfo.CurrentCulture);
    }
}
