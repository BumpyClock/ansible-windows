using System.Text;

namespace Ansible.Core;

public sealed record SpeakerActivity(string SpeakerId, long StartSample, long EndSample);

/// <summary>How an ASR family stamps its tokens, which decides where a word's speaker is looked up.</summary>
public enum TokenTimingModel
{
    /// <summary>Each token carries the 80 ms frame on which the decoder emitted it (Nemotron).</summary>
    EmissionFrame,
    /// <summary>A word opens where the previous word closed and closes where the next one opens (Kroko).</summary>
    WordSpan
}

public sealed class NemotronSpeakerAttribution
{
    private SpeakerActivity[] _activities = [];
    private ActivityTimeline? _timeline;
    private const long SampleRate = 16000;
    private const string UnknownSpeaker = "Unknown speaker";
    internal const long MaximumSamples = SampleRate * 120 * 60;
    internal const int MaximumTokens = 262144;
    internal const int MaximumActivities = 100000;
    internal const int MaximumTextCharacters = 4 * 1024 * 1024;
    // ASR tokens are stamped when the decoder emits them, after the audio they describe. Real timing on
    // 2026-10-22: Nemotron closed a phrase 30 ms after the diarizer's activity ended, Kroko 150 ms after.
    internal const long EmissionLagSamples = SampleRate * 32 / 100;
    // A Kroko span opens after its word has begun: on 2026-10-22 "Some" was spoken at 0.40 s and its span
    // opened at 0.84 s, so the speech up to 450 ms before the opening still belongs to the span's word.
    internal const long WordSpanStartLagSamples = SampleRate * 45 / 100;
    // Diarizer activity is reported in 80 ms frames; a gap of up to two frames is one run of speech.
    internal const long MaximumRunGapSamples = 2 * 1280;

    public TranscriptUpdate Create(IReadOnlyList<MeetingTimedToken> tokens,
        IReadOnlyList<SpeakerActivity> activities, long processedSamples, bool isFinal,
        TokenTimingModel timing = TokenTimingModel.EmissionFrame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(activities);
        cancellationToken.ThrowIfCancellationRequested();
        if (processedSamples is < 0 or > MaximumSamples)
            throw new ArgumentOutOfRangeException(nameof(processedSamples));
        if (tokens.Count > MaximumTokens)
            throw new ArgumentOutOfRangeException(nameof(tokens));
        if (activities.Count > MaximumActivities)
            throw new ArgumentOutOfRangeException(nameof(activities));

        var timeline = Timeline(activities, processedSamples, cancellationToken);
        var turns = new List<MeetingTurn>();
        var turnText = new StringBuilder();
        var turnSpeaker = UnknownSpeaker;
        long turnStart = 0, turnEnd = 0;
        var turnQuality = MeetingTimingQuality.Unknown;
        var speech = new StringBuilder();
        var wordText = new StringBuilder();
        long previousStart = 0;
        var lexical = new WordSpan();
        var every = new WordSpan();
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token is null || token.Text is null || token.StartSample < previousStart ||
                token.EndSample < token.StartSample)
                throw new ArgumentException("Tokens require text and nonnegative, ordered native sample bounds.", nameof(tokens));
            if (token.Text.Length > MaximumTextCharacters - speech.Length)
                throw new ArgumentException("Meeting speech exceeds the supported text limit.", nameof(tokens));
            previousStart = token.StartSample;
            speech.Append(token.Text);
            var offset = 0;
            while (offset < token.Text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (char.IsWhiteSpace(token.Text[offset])) FinishWord();
                var end = offset;
                while (end < token.Text.Length && char.IsWhiteSpace(token.Text[end])) end++;
                var contentStart = end;
                while (end < token.Text.Length && !char.IsWhiteSpace(token.Text[end])) end++;
                if (end > contentStart)
                {
                    every.Extend(token, processedSamples);
                    // Punctuation is emitted when the decoder closes a phrase, often a pause later; only
                    // letters and digits say when the word itself was heard.
                    if (HasLexicalContent(token.Text.AsSpan(contentStart, end - contentStart)))
                        lexical.Extend(token, processedSamples);
                }
                // Do not invent sub-token timestamps when a decoded piece contains several words.
                wordText.Append(token.Text, offset, end - offset);
                offset = end;
            }
        }
        FinishWord();

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
            if (every.HasContent)
            {
                var word = lexical.HasContent ? lexical : every;
                var quality = word.HasNativeTiming && word.End > word.Start
                    ? MeetingTimingQuality.Aligned : MeetingTimingQuality.Unknown;
                var speaker = quality == MeetingTimingQuality.Aligned
                    ? timeline.GetSpeaker(word.Start, word.End, timing) : UnknownSpeaker;
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
                turnText.Append(wordText);
            }
            wordText.Clear();
            lexical.Reset();
            every.Reset();
        }

        void FinishTurn()
        {
            if (turnText.Length == 0) return;
            turns.Add(new(turnSpeaker, turnStart / (double)SampleRate, turnEnd / (double)SampleRate,
                turnText.ToString().Trim()) { TimingQuality = turnQuality });
            turnText.Clear();
        }
    }

    // Activities are revisable, including the final snapshot. Compare their values, not list identity.
    private ActivityTimeline Timeline(IReadOnlyList<SpeakerActivity> activities, long processedSamples, CancellationToken token)
    {
        var unchanged = _timeline is not null && activities.Count == _activities.Length;
        for (var i = 0; unchanged && i < activities.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            unchanged = activities[i] == _activities[i];
        }
        if (unchanged)
        {
            if (_timeline!.EndSample > processedSamples)
                throw new ArgumentException("Speaker activities exceed the processed audio.", nameof(activities));
            return _timeline;
        }
        var snapshot = activities.ToArray();
        var timeline = new ActivityTimeline(snapshot, processedSamples, token);
        _activities = snapshot;
        return _timeline = timeline;
    }

    internal static int GetSpeakerIndex(SpeakerActivity activity, long processedSamples)
    {
        if (activity is null || activity.SpeakerId is not { Length: 9 } id ||
            !id.StartsWith("speaker_", StringComparison.Ordinal) || id[8] is < '0' or > '7' ||
            activity.StartSample < 0 || activity.EndSample <= activity.StartSample ||
            activity.EndSample > processedSamples)
            throw new ArgumentException("Activities require speaker_0 through speaker_7 and positive, in-range native intervals.",
                nameof(activity));
        return id[8] - '0';
    }

    private static bool HasLexicalContent(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
            if (char.IsLetterOrDigit(character)) return true;
        return false;
    }

    private sealed class WordSpan
    {
        public bool HasContent { get; private set; }
        public long Start { get; private set; }
        public long End { get; private set; }
        public bool HasNativeTiming { get; private set; }

        public void Extend(MeetingTimedToken token, long processedSamples)
        {
            if (!HasContent)
            {
                Start = Math.Min(token.StartSample, processedSamples);
                End = Start;
                HasNativeTiming = true;
                HasContent = true;
            }
            End = Math.Max(End, Math.Min(token.EndSample, processedSamples));
            HasNativeTiming &= token.EndSample <= processedSamples;
        }

        public void Reset() => HasContent = false;
    }

    private readonly record struct ActivityEvent(long Sample, int Speaker, int Delta);

    // Prefix coverage of the activity union avoids rescanning activities for every word.
    private sealed class ActivityTimeline
    {
        private readonly long[] _samples;
        private readonly int[] _masks;
        private readonly long[,] _coverage;
        private readonly int _count;
        public long EndSample { get; private set; }

        public ActivityTimeline(IReadOnlyList<SpeakerActivity> activities, long processedSamples,
            CancellationToken cancellationToken)
        {
            var events = new ActivityEvent[activities.Count * 2];
            for (var i = 0; i < activities.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activity = activities[i];
                var speaker = GetSpeakerIndex(activity, processedSamples);
                EndSample = Math.Max(EndSample, activity.EndSample);
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

        public string GetSpeaker(long start, long end, TokenTimingModel timing) => timing switch
        {
            // Emission frames sit inside or just after the word. A frame with no diarized speech was usually
            // emitted just after its speaker stopped, so the emission allowance before it decides.
            TokenTimingModel.EmissionFrame => TryGetMajority(start, end)
                ?? (start > 0 ? TryGetMajority(Math.Max(0, start - EmissionLagSamples), start) : null)
                ?? UnknownSpeaker,
            // A word span opens up to one start lag into the word and can run through the following
            // pause into the next speaker's onset, so the speech around its opening decides. When the
            // emission allowance around the opening is silent, the rest of the start lag before it is the
            // word's own trailing speech. A span that opened in silence (the previous word closed on a
            // pause) holds its own word as the first run of speech inside it; later runs belong to the next word.
            TokenTimingModel.WordSpan => TryGetMajority(Math.Max(0, start - EmissionLagSamples), Math.Min(end, start + EmissionLagSamples))
                ?? (start > EmissionLagSamples ? TryGetMajority(Math.Max(0, start - WordSpanStartLagSamples), start - EmissionLagSamples) : null)
                ?? TryGetFirstRunMajority(start, end)
                ?? UnknownSpeaker,
            _ => throw new ArgumentOutOfRangeException(nameof(timing))
        };

        // Majority over the first run of diarized speech inside the span, or null when there is none.
        private string? TryGetFirstRunMajority(long start, long end)
        {
            long runStart = -1, runEnd = -1;
            for (var point = Math.Max(0, FindPoint(start)); point < _count && _samples[point] < end; point++)
            {
                var segmentStart = Math.Max(_samples[point], start);
                var segmentEnd = Math.Min(point + 1 < _count ? _samples[point + 1] : long.MaxValue, end);
                if (_masks[point] != 0)
                {
                    if (runStart < 0) runStart = segmentStart;
                    runEnd = segmentEnd;
                }
                else if (runStart >= 0 && segmentEnd - segmentStart > MaximumRunGapSamples) break;
            }
            return runStart < 0 ? null : TryGetMajority(runStart, runEnd);
        }

        // Null means the span holds no diarized speech at all; overlap and ties are Unknown.
        private string? TryGetMajority(long start, long end)
        {
            var startPoint = FindPoint(start);
            var endPoint = FindPoint(end);
            if (CoverageAt(endPoint, end, 8) != CoverageAt(startPoint, start, 8))
                return UnknownSpeaker;
            var best = -1;
            long bestCoverage = 0, covered = 0;
            for (var speaker = 0; speaker < 8; speaker++)
            {
                var coverage = CoverageAt(endPoint, end, speaker) - CoverageAt(startPoint, start, speaker);
                covered += coverage;
                if (coverage > bestCoverage)
                {
                    best = speaker;
                    bestCoverage = coverage;
                }
            }
            if (covered == 0) return null;
            // Silence inside the span does not vote: a strict majority of the diarized speech decides.
            return bestCoverage * 2 > covered ? $"Speaker {best}" : UnknownSpeaker;
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
