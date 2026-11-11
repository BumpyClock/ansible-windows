namespace Ansible.Core;

/// <summary>Splits cumulative speech without inventing word timings. Each stable prefix closes at the prior update boundary.</summary>
internal sealed class CumulativeMeetingChunks
{
    private readonly List<(int TextEnd, long EndSample)> _boundaries = [];
    private string _previous = "";
    private long _previousSamples;

    public MeetingTurn[] Update(string speech, long processedSamples, bool final)
    {
        ArgumentNullException.ThrowIfNull(speech);
        if (processedSamples < _previousSamples) throw new ArgumentOutOfRangeException(nameof(processedSamples));
        var text = speech.Trim();
        var stable = SharedBoundary(_previous, text);
        _boundaries.RemoveAll(boundary => boundary.TextEnd > stable);
        var lastEnd = _boundaries.Count == 0 ? 0 : _boundaries[^1].TextEnd;
        if (_previous.Length > 0 && stable > lastEnd && stable < text.Length && _previousSamples > 0)
            _boundaries.Add((stable, _previousSamples));
        _previous = text;
        _previousSamples = processedSamples;

        var turns = new List<MeetingTurn>();
        var start = 0;
        long startSample = 0;
        foreach (var boundary in _boundaries)
        {
            Add(text[start..boundary.TextEnd], startSample, boundary.EndSample);
            start = boundary.TextEnd;
            startSample = boundary.EndSample;
        }
        Add(text[start..], startSample, processedSamples);
        for (var index = 0; index < turns.Count; index++)
            turns[index] = turns[index] with { IsProvisional = !final && index == turns.Count - 1 };
        return [.. turns];

        void Add(string piece, long from, long to)
        {
            piece = piece.Trim();
            if (piece.Length == 0 || to < from) return;
            turns.Add(new MeetingTurn("You", from / 16000d, to / 16000d, piece)
            {
                TimingQuality = MeetingTimingQuality.Chunk,
                Source = MeetingAudioSource.Microphone
            });
        }
    }

    private static int SharedBoundary(string previous, string current)
    {
        var maximum = Math.Min(previous.Length, current.Length);
        var index = 0;
        while (index < maximum && previous[index] == current[index]) index++;
        if (index == maximum && previous.Length == current.Length) return index;
        if (index == previous.Length && index < current.Length && char.IsWhiteSpace(current[index])) return index;
        while (index > 0 && !char.IsWhiteSpace(previous[index - 1])) index--;
        return index;
    }
}
