namespace Ansible.Core;

public sealed class MeetingSpeakerMatcher
{
    private const int MaximumObservations = 15000;
    private const long SamplesPerSecond = 16000;
    private const long MaximumGapSamples = SamplesPerSecond * 3 / 2;
    private readonly object _gate = new();
    private readonly SortedDictionary<long, MeetingSpeakerObservation> _observations = [];
    private bool _overflowed;

    public void Observe(MeetingSpeakerObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SamplePosition < 0) throw new ArgumentOutOfRangeException(nameof(observation));
        var names = observation.SpeakingNames?.ToArray() ?? [];
        var snapshot = observation with { SpeakingNames = names };
        lock (_gate)
        {
            if (_overflowed) return;
            if (_observations.TryGetValue(snapshot.SamplePosition, out var previous))
            {
                if (previous.Status != snapshot.Status || previous.SelfName != snapshot.SelfName ||
                    !previous.SpeakingNames.SequenceEqual(names))
                    snapshot = snapshot with { Status = MeetingSpeakerReadStatus.Ambiguous };
            }
            else if (_observations.Count == MaximumObservations)
            {
                _overflowed = true;
                _observations.Clear();
                return;
            }
            _observations[snapshot.SamplePosition] = snapshot;
        }
    }

    public MeetingDocument Refine(MeetingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        MeetingSpeakerObservation[] observations;
        lock (_gate) observations = _overflowed ? [] : _observations.Values.ToArray();
        var evidence = new Dictionary<string, SpeakerEvidence>(StringComparer.Ordinal);
        var sortedTurns = document.Turns.OrderBy(turn => turn.StartSeconds).ThenBy(turn => turn.EndSeconds).ToArray();
        var overlapping = new bool[sortedTurns.Length];
        var furthestEnd = double.NegativeInfinity;
        var furthestIndex = -1;
        for (var index = 0; index < sortedTurns.Length; index++)
        {
            var turn = sortedTurns[index];
            if (turn.StartSeconds < furthestEnd)
            {
                overlapping[index] = true;
                overlapping[furthestIndex] = true;
            }
            if (turn.EndSeconds > furthestEnd) { furthestEnd = turn.EndSeconds; furthestIndex = index; }
        }
        for (var index = 0; index < sortedTurns.Length; index++)
        {
            var turn = sortedTurns[index];
            if (overlapping[index] || turn.IsProvisional || turn.SpeakerId == "Unknown speaker" ||
                turn.TimingQuality != MeetingTimingQuality.Aligned ||
                turn.Source == MeetingAudioSource.Mixed || turn.EndSeconds - turn.StartSeconds < 1)
                continue;
            var result = MatchWindow(turn, observations);
            if (result.Name is null && !result.Conflicting) continue;
            if (!evidence.TryGetValue(turn.SpeakerId, out var speaker)) evidence[turn.SpeakerId] = speaker = new();
            speaker.Conflicting |= result.Conflicting;
            if (result.Name is { } name)
            {
                speaker.Names.Add(name);
                speaker.ConsistentTurns++;
            }
        }

        var namesBySpeaker = evidence.Where(pair => !pair.Value.Conflicting && pair.Value.Names.Count == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Names.Single(), StringComparer.Ordinal);
        var ambiguousNames = namesBySpeaker.Values.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var automatic = namesBySpeaker.Where(pair => evidence[pair.Key].ConsistentTurns >= 2 && !ambiguousNames.Contains(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return document with { AutoSpeakerNames = automatic };
    }

    private static (string? Name, bool Conflicting) MatchWindow(MeetingTurn turn, MeetingSpeakerObservation[] observations)
    {
        var start = turn.StartSeconds * SamplesPerSecond;
        var end = turn.EndSeconds * SamplesPerSecond;
        var first = UpperBound(observations, start) - 1;
        var last = LowerBound(observations, end);
        if (first < 0 || last >= observations.Length || start - observations[first].SamplePosition > MaximumGapSamples ||
            observations[last].SamplePosition - end > MaximumGapSamples)
            return (null, false);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var covered = true;
        for (var index = first; index <= last; index++)
        {
            var observation = observations[index];
            if (index > first && observation.SamplePosition - observations[index - 1].SamplePosition > MaximumGapSamples)
                covered = false;
            var name = GetCandidate(observation, turn.Source);
            if (name is null) covered = false;
            else names.Add(name);
        }
        return names.Count > 1 ? (null, true) : covered && names.Count == 1 ? (names.Single(), false) : (null, false);
    }

    private static string? GetCandidate(MeetingSpeakerObservation observation, MeetingAudioSource source)
    {
        if (observation.Status != MeetingSpeakerReadStatus.Readable || observation.SpeakingNames.Count > 1) return null;
        if (source == MeetingAudioSource.Microphone)
        {
            var self = observation.SelfName?.Trim();
            if (string.IsNullOrWhiteSpace(self)) return null;
            if (observation.SpeakingNames.Count == 1 && !string.Equals(observation.SpeakingNames[0]?.Trim(), self, StringComparison.OrdinalIgnoreCase)) return null;
            return self;
        }
        if (source != MeetingAudioSource.Playback || observation.SpeakingNames.Count != 1) return null;
        var name = observation.SpeakingNames[0]?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, observation.SelfName?.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return name;
    }

    private static int LowerBound(MeetingSpeakerObservation[] observations, double position)
    {
        var low = 0;
        var high = observations.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (observations[middle].SamplePosition < position) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(MeetingSpeakerObservation[] observations, double position)
    {
        var low = LowerBound(observations, position);
        return low < observations.Length && observations[low].SamplePosition == position ? low + 1 : low;
    }

    private sealed class SpeakerEvidence
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ConsistentTurns { get; set; }
        public bool Conflicting { get; set; }
    }
}
