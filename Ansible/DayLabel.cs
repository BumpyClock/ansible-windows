using System.Globalization;

namespace Ansible;

/// <summary>The day headings shared by Home history and Meetings.</summary>
internal static class DayLabel
{
    /// <summary>TODAY, YESTERDAY, then the date. The year appears only for dates outside the current year.</summary>
    public static string For(DateOnly date, DateOnly today)
    {
        if (date == today) return "TODAY";
        if (date == today.AddDays(-1)) return "YESTERDAY";
        var format = date.Year == today.Year ? "ddd, MMM d" : "ddd, MMM d, yyyy";
        return date.ToString(format, CultureInfo.CurrentCulture).ToUpperInvariant();
    }
}
