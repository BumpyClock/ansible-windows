using System.Globalization;

namespace Ansible;

/// <summary>The day headings shared by Home history and Meetings.</summary>
internal static class DayLabel
{
    /// <summary>
    /// Today, Yesterday, then the date in sentence case, day before month as every other date in the app. The year
    /// appears only for dates outside the current year.
    /// </summary>
    public static string For(DateOnly date, DateOnly today)
    {
        if (date == today) return "Today";
        if (date == today.AddDays(-1)) return "Yesterday";
        return Date(date, today);
    }

    /// <summary>Weekday, day, and abbreviated month, with the year only for dates outside the current year.</summary>
    public static string Date(DateOnly date, DateOnly today) =>
        date.ToString(date.Year == today.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.CurrentCulture);
}
