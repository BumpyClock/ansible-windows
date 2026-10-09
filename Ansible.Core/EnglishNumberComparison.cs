using System.Globalization;
using System.Text.RegularExpressions;

namespace Ansible.Core;

// Comparison only: original transcript text and native token indices remain untouched.
internal static partial class EnglishNumberComparison
{
    internal sealed record Number(string Original, string Spoken);
    internal sealed record Expansion(string Text, IReadOnlyList<Number> Numbers);

    public static Expansion? Expand(string text)
    {
        var numbers = new List<Number>();
        var expanded = Numeral().Replace(text, match =>
        {
            var before = match.Index == 0 ? '\0' : text[match.Index - 1];
            var end = match.Index + match.Length;
            var after = end == text.Length ? '\0' : text[end];
            if (!(before == '\0' || char.IsWhiteSpace(before) || before is '(' or '[' or '{') ||
                !(after == '\0' || char.IsWhiteSpace(after) || after is ')' or ']' or '}' or '.' or ',' or ';' or '!' or '?'))
                return match.Value;
            // A punctuation-delimited identifier or malformed numeric continuation is not a quantity.
            if (end + 1 < text.Length && after is '.' or ',' && char.IsLetterOrDigit(text[end + 1])) return match.Value;
            var parts = match.Value.Split('.');
            if (parts.Length > 2 || (parts.Length == 2 && parts[1].Length is < 1 or > 6)) return match.Value;
            var groups = parts[0].Split(',');
            if (groups.Length > 1 && (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(group => group.Length != 3))) return match.Value;
            var whole = string.Concat(groups);
            if (whole.Length > 1 && whole[0] == '0' || !uint.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 999999999)
                return match.Value;
            var spoken = Integer(value);
            if (parts.Length == 2) spoken += " point " + string.Join(" ", parts[1].Select(digit => Units[digit - '0']));
            numbers.Add(new Number(match.Value, spoken));
            return spoken;
        });
        return numbers.Count == 0 ? null : new Expansion(expanded, numbers);
    }

    private static readonly string[] Units = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static string Integer(uint value)
    {
        if (value < 20) return Units[value];
        if (value < 100) return Tens[value / 10] + (value % 10 == 0 ? "" : " " + Integer(value % 10));
        if (value < 1000) return Integer(value / 100) + " hundred" + (value % 100 == 0 ? "" : " " + Integer(value % 100));
        var divisor = value < 1000000 ? 1000u : 1000000u;
        return Integer(value / divisor) + (divisor == 1000 ? " thousand" : " million") + (value % divisor == 0 ? "" : " " + Integer(value % divisor));
    }

    [GeneratedRegex("[0-9]+(?:,[0-9]+)*(?:\\.[0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Numeral();
}
