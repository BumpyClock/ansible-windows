using System.Text.RegularExpressions;

namespace Ansible.Core;

public sealed partial class MeetingTranscript
{
    private IReadOnlyList<MeetingTurn> _turns = [];
    private double _processedSeconds;

    public IReadOnlyList<MeetingTurn> Update(string cumulativeText, double processedSeconds, bool isFinal = false)
    {
        ArgumentNullException.ThrowIfNull(cumulativeText);
        if (!double.IsFinite(processedSeconds) || processedSeconds < _processedSeconds || processedSeconds > TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentOutOfRangeException(nameof(processedSeconds));

        var text = cumulativeText.TrimEnd();
        if (!isFinal) text = IncompleteLabel().Replace(text, "");
        var labels = SpeakerLabel().Matches(text);
        var parsed = new List<(string SpeakerId, string Text)>();
        var start = 0;
        var speakerId = "Unknown speaker";
        foreach (Match label in labels)
        {
            AddText(text[start..label.Index]);
            speakerId = "Speaker " + NormalizeNumber(label.Groups[1].Value);
            start = label.Index + label.Length;
        }
        AddText(text[start..]);

        var turns = new MeetingTurn[parsed.Count];
        for (var index = 0; index < parsed.Count; index++)
        {
            var item = parsed[index];
            var previous = index < _turns.Count && _turns[index].SpeakerId == item.SpeakerId ? _turns[index] : null;
            // Native text has no word times. Retain approximate processing-window bounds across cumulative updates.
            turns[index] = new MeetingTurn(item.SpeakerId, previous?.StartSeconds ?? _processedSeconds,
                previous?.Text == item.Text ? previous.EndSeconds : processedSeconds, item.Text, !isFinal);
        }
        _processedSeconds = processedSeconds;
        _turns = turns;
        return turns;

        void AddText(string value)
        {
            value = value.Trim();
            if (value.Length > 0) parsed.Add((speakerId, value));
        }
    }

    private static string NormalizeNumber(string number)
    {
        var trimmed = number.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    [GeneratedRegex(@"(?<!\S)Speaker\s*([0-9]+)\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex SpeakerLabel();

    [GeneratedRegex(@"(?<!\S)(?:S|Sp|Spe|Spea|Speak|Speake|Speaker(?:\s*[0-9]*)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex IncompleteLabel();
}
