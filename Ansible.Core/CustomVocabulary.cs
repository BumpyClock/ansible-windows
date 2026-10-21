using System.Globalization;
using System.Text;

namespace Ansible.Core;

public static class CustomVocabulary
{
    public const int MaximumEntries = 100;
    public const int MaximumEntryCharacters = 100;
    public const int MaximumUtf8Bytes = 4096;

    public static string? ModelCompatibilityError(AudioModel? model, string dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        if (model?.Family == "kroko_asr" && dictionary.Contains('/'))
        {
            return "Kroko cannot use saved entries containing '/'. Edit Dictionary to remove '/' or choose another model.";
        }
        return null;
    }

    public static string AddEntries(string dictionary, string additions)
    {
        var existing = Normalize(dictionary).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var added = Normalize(additions).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return Normalize(string.Join("\n", existing.Concat(added).Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    public static string RemoveEntry(string dictionary, string entry)
    {
        var entries = Normalize(dictionary).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (!entries.Contains(entry, StringComparer.Ordinal))
            throw new InvalidOperationException("That word is no longer in your dictionary.");
        return string.Join("\n", entries.Where(value => value != entry));
    }

    public static string ReplaceEntry(string dictionary, string entry, string replacement)
    {
        var entries = Normalize(dictionary).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(entries, entry);
        if (index < 0) throw new InvalidOperationException("That word is no longer in your dictionary.");
        var normalized = Normalize(replacement);
        if (normalized.Length == 0 || normalized.Contains('\n'))
            throw new InvalidDataException("Enter one word or phrase.");
        if (entries.Where((_, position) => position != index).Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("That word is already in your dictionary.");
        entries[index] = normalized;
        return Normalize(string.Join("\n", entries));
    }

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
            if (new StringInfo(entry).LengthInTextElements > MaximumEntryCharacters || entry.Any(char.IsControl))
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
