namespace Ansible;

/// <summary>Line-breaking help for wrapped help text.</summary>
internal static class TextFlow
{
    /// <summary>
    /// Joins the last two words with a no-break space, so wrapped text never ends in a one-word line.
    /// </summary>
    public static string NoWidow(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var last = text.TrimEnd().LastIndexOf(' ');
        return last <= 0 ? text : string.Concat(text.AsSpan(0, last), "\u00A0", text.AsSpan(last + 1));
    }
}
