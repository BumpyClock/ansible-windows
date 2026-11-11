using System.Globalization;

namespace Ansible;

internal static class DayLabel
{
    public static string For(DateOnly date, DateOnly today)
    {
        if (date == today) return "Today";
        if (date == today.AddDays(-1)) return "Yesterday";
        return Date(date, today);
    }

    public static string Date(DateOnly date, DateOnly today) =>
        date.ToString(date.Year == today.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.CurrentCulture);
}
