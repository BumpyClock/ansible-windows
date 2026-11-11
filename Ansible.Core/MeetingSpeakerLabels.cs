using System.Text.RegularExpressions;

namespace Ansible.Core;

internal readonly record struct MeetingSpeakerLabel(int Index, int Length, string SpeakerId);

internal static partial class MeetingSpeakerLabels
{
    public static IReadOnlyList<MeetingSpeakerLabel> Find(string text) => SpeakerLabel().Matches(text)
        .Select(label => new MeetingSpeakerLabel(label.Index, label.Length, "Speaker " + NormalizeNumber(label.Groups[1].Value))).ToArray();

    private static string NormalizeNumber(string number)
    {
        var trimmed = number.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    // VibeVoice can omit whitespace between a sentence and its next speaker label.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])Speaker\s*([0-9]+)\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex SpeakerLabel();
}
