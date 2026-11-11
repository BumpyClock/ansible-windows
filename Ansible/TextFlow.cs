namespace Ansible;

internal static class TextFlow
{
    public static string NoWidow(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var last = text.TrimEnd().LastIndexOf(' ');
        return last <= 0 ? text : string.Concat(text.AsSpan(0, last), "\u00A0", text.AsSpan(last + 1));
    }
}
