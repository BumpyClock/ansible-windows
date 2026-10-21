using System.Text;

namespace Ansible.Core;

public sealed record SpeakerActivity(string SpeakerId, long StartSample, long EndSample);

public static class NemotronSpeakerAttribution
{
    private const long SampleRate = 16000;
    private const string UnknownSpeaker = "Unknown speaker";

    public static TranscriptUpdate Create(IReadOnlyList<MeetingTimedToken> tokens,
        IReadOnlyList<SpeakerActivity> activities, long processedSamples, bool isFinal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(activities);
        cancellationToken.ThrowIfCancellationRequested();
        if (processedSamples is < 0 or > SampleRate * 120 * 60)
            throw new ArgumentOutOfRangeException(nameof(processedSamples));
        if (tokens.Count > 262144)
            throw new ArgumentOutOfRangeException(nameof(tokens));
        if (activities.Count > 100000)
            throw new ArgumentOutOfRangeException(nameof(activities));

        var words = new List<Word>();
        var speech = new StringBuilder();
        var wordText = new StringBuilder();
        long previousStart = 0, wordStart = 0, wordEnd = 0;
        var hasNativeTiming = true;
        for (var i = 0; i < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = tokens[i];
            if (token is null || token.Text is null || token.StartSample < previousStart ||
                token.EndSample < token.StartSample)
                throw new ArgumentException("Tokens require text and nonnegative, ordered native sample bounds.", nameof(tokens));
            previousStart = token.StartSample;
            speech.Append(token.Text);
            if (token.Text.Length == 0) continue;
            if (char.IsWhiteSpace(token.Text[0])) FinishWord();
            if (wordText.Length == 0)
            {
                wordStart = Math.Min(token.StartSample, processedSamples);
                wordEnd = wordStart;
                hasNativeTiming = true;
            }
            wordText.Append(token.Text);
            wordEnd = Math.Max(wordEnd, Math.Min(token.EndSample, processedSamples));
            hasNativeTiming &= token.EndSample <= processedSamples;
        }
        FinishWord();

        var timeline = new ActivityTimeline(activities, processedSamples, cancellationToken);
        var turns = new List<MeetingTurn>();
        var turnText = new StringBuilder();
        var turnSpeaker = UnknownSpeaker;
        long turnStart = 0, turnEnd = 0;
        var turnQuality = MeetingTimingQuality.Unknown;
        foreach (var word in words)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quality = word.HasNativeTiming && word.End > word.Start
                ? MeetingTimingQuality.Aligned : MeetingTimingQuality.Unknown;
            var speaker = quality == MeetingTimingQuality.Aligned
                ? timeline.GetSpeaker(word.Start, word.End) : UnknownSpeaker;
            if (turnText.Length > 0 && (speaker != turnSpeaker || quality != turnQuality ||
                word.Start - turnEnd > SampleRate || Math.Max(turnEnd, word.End) - turnStart > 10 * SampleRate))
                FinishTurn();
            if (turnText.Length == 0)
            {
                turnSpeaker = speaker;
                turnStart = word.Start;
                turnEnd = word.End;
                turnQuality = quality;
            }
            turnEnd = Math.Max(turnEnd, word.End);
            turnText.Append(word.Text);
        }
        FinishTurn();
        if (!isFinal && turns.Count > 0)
            turns[^1] = turns[^1] with { IsProvisional = true };
        cancellationToken.ThrowIfCancellationRequested();
        var text = speech.ToString().Trim();
        return new TranscriptUpdate(text, isFinal, text)
        {
            ProcessedSamples = processedSamples,
            SpeakerTurns = turns.ToArray()
        };

        void FinishWord()
        {
            if (wordText.Length == 0) return;
            var text = wordText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                words.Add(new(text, wordStart, wordEnd, hasNativeTiming));
            wordText.Clear();
        }

        void FinishTurn()
        {
            if (turnText.Length == 0) return;
            turns.Add(new(turnSpeaker, turnStart / (double)SampleRate, turnEnd / (double)SampleRate,
                turnText.ToString().Trim()) { TimingQuality = turnQuality });
            turnText.Clear();
        }
    }

    private sealed record Word(string Text, long Start, long End, bool HasNativeTiming);
    private readonly record struct ActivityEvent(long Sample, int Speaker, int Delta);

    // Prefix coverage of the activity union allows arbitrary word ends without rescanning activities.
    private sealed class ActivityTimeline
    {
        private readonly long[] _samples;
        private readonly int[] _masks;
        private readonly long[,] _coverage;
        private readonly int _count;

        public ActivityTimeline(IReadOnlyList<SpeakerActivity> activities, long processedSamples,
            CancellationToken cancellationToken)
        {
            var events = new ActivityEvent[activities.Count * 2];
            for (var i = 0; i < activities.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activity = activities[i];
                if (activity is null || activity.SpeakerId is not { Length: 9 } id ||
                    !id.StartsWith("speaker_", StringComparison.Ordinal) || id[8] is < '0' or > '7' ||
                    activity.StartSample < 0 || activity.EndSample <= activity.StartSample ||
                    activity.EndSample > processedSamples)
                    throw new ArgumentException("Activities require speaker_0 through speaker_7 and positive, in-range native intervals.",
                        nameof(activities));
                var speaker = id[8] - '0';
                events[2 * i] = new(activity.StartSample, speaker, 1);
                events[2 * i + 1] = new(activity.EndSample, speaker, -1);
            }
            Array.Sort(events, static (left, right) => left.Sample.CompareTo(right.Sample));
            cancellationToken.ThrowIfCancellationRequested();
            _samples = new long[events.Length];
            _masks = new int[events.Length];
            _coverage = new long[9, events.Length];
            var active = new int[8];
            var mask = 0;
            var point = 0;
            for (var i = 0; i < events.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = events[i].Sample;
                _samples[point] = sample;
                if (point > 0)
                {
                    var elapsed = sample - _samples[point - 1];
                    for (var speaker = 0; speaker < 9; speaker++)
                    {
                        var covered = speaker == 8 ? IsSimultaneous(mask) : (mask & (1 << speaker)) != 0;
                        _coverage[speaker, point] = _coverage[speaker, point - 1] + (covered ? elapsed : 0);
                    }
                }
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    active[events[i].Speaker] += events[i].Delta;
                    i++;
                } while (i < events.Length && events[i].Sample == sample);
                mask = 0;
                for (var speaker = 0; speaker < 8; speaker++)
                    if (active[speaker] > 0) mask |= 1 << speaker;
                _masks[point++] = mask;
            }
            _count = point;
        }

        public string GetSpeaker(long start, long end)
        {
            var startPoint = FindPoint(start);
            var endPoint = FindPoint(end);
            if (CoverageAt(endPoint, end, 8) != CoverageAt(startPoint, start, 8))
                return UnknownSpeaker;
            // A strict majority of the entire word, not merely its covered portion, is required.
            for (var speaker = 0; speaker < 8; speaker++)
                if (CoverageAt(endPoint, end, speaker) - CoverageAt(startPoint, start, speaker) > (end - start) / 2)
                    return $"Speaker {speaker}";
            return UnknownSpeaker;
        }

        private int FindPoint(long sample)
        {
            var index = Array.BinarySearch(_samples, 0, _count, sample);
            return index >= 0 ? index : ~index - 1;
        }

        private long CoverageAt(int point, long sample, int speaker)
        {
            if (point < 0) return 0;
            var covered = speaker == 8 ? IsSimultaneous(_masks[point]) : (_masks[point] & (1 << speaker)) != 0;
            return _coverage[speaker, point] + (covered ? sample - _samples[point] : 0);
        }

        private static bool IsSimultaneous(int mask) => (mask & (mask - 1)) != 0;
    }
}
