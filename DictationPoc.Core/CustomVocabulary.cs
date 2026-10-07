using System.Text;

namespace DictationPoc.Core;

public static class CustomVocabulary
{
    public const int MaximumEntries = 100;
    public const int MaximumEntryCharacters = 100;
    public const int MaximumUtf8Bytes = 4096;

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumUtf8Bytes * 2)
            throw new InvalidDataException("The dictionary is too large. Keep it within 4 KB of text.");
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None))
        {
            var entry = string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Normalize(NormalizationForm.FormC);
            if (entry.Length == 0) { continue; }
            if (entry.Length > MaximumEntryCharacters || entry.Any(char.IsControl))
                throw new InvalidDataException("Each dictionary entry must be at most 100 characters and contain no control characters.");
            if (seen.Add(entry)) { entries.Add(entry); }
            if (entries.Count > MaximumEntries)
                throw new InvalidDataException("The dictionary can contain at most 100 unique words or phrases.");
        }
        var normalized = string.Join("\n", entries);
        if (Encoding.UTF8.GetByteCount(normalized) > MaximumUtf8Bytes)
            throw new InvalidDataException("The dictionary is too large. Keep it within 4 KB of UTF-8 text.");
        return normalized;
    }
}
