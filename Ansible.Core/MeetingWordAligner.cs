using System.Text;

namespace Ansible.Core;

public interface IMeetingWordAligner : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MeetingTurn>> AlignAsync(WaveAudio audio, IReadOnlyList<MeetingTurn> turns, CancellationToken cancellationToken);
}

public sealed class NativeMeetingWordAligner : IMeetingWordAligner
{
    private readonly NativeAudioEngine _engine;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private AudioModel? _model;
    private bool _disposed;

    public NativeMeetingWordAligner(string libraryPath, string catalogPath, string modelsDirectory,
        int threads = 4, int memoryHeadroomMB = 512)
    {
        _engine = new NativeAudioEngine(libraryPath, catalogPath, modelsDirectory, threads, memoryHeadroomMB);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_model is not null) return;
            var models = await _engine.ConnectAsync(cancellationToken);
            var candidate = models.FirstOrDefault(model => model.Family == "nemotron_asr")
                ?? throw new InvalidOperationException("Optional timestamp alignment requires installed Nemotron ASR weights. Approximate chunk times will be kept.");
            await _engine.PrepareAlignmentAsync(candidate, cancellationToken);
            _model = candidate;
        }
        finally { _commands.Release(); }
    }

    public async Task<IReadOnlyList<MeetingTurn>> AlignAsync(WaveAudio audio, IReadOnlyList<MeetingTurn> turns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turns);
        await _commands.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var model = _model ?? throw new InvalidOperationException("Initialize the optional timestamp aligner before aligning speech.");
            var tokens = await _engine.ReadAlignmentTokensAsync(model, audio, cancellationToken);
            return MeetingWordAlignment.Align(turns, tokens, audio.Samples.Length / (double)audio.SampleRate, cancellationToken);
        }
        finally { _commands.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync();
        try
        {
            if (_disposed) return;
            await _engine.DisposeAsync();
            _disposed = true;
            _model = null;
        }
        finally { _commands.Release(); }
    }
}

public sealed record MeetingTimedToken(string Text, long StartSample, long EndSample);

public static class MeetingWordAlignment
{
    private const int SampleRate = 16000;
    private const int MaximumTokens = 8192;
    private const int MaximumTextCharacters = 65536;

    public static IReadOnlyList<MeetingTurn> Align(IReadOnlyList<MeetingTurn> turns,
        IReadOnlyList<MeetingTimedToken> tokens, double clipDurationSeconds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(tokens);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(clipDurationSeconds) || clipDurationSeconds is <= 0 or > 30 ||
            tokens.Count is 0 or > MaximumTokens || turns.Count > 512)
            return turns;
        var words = ReadWords(tokens, clipDurationSeconds);
        if (words is null || words.Count == 0) return turns;
        var firstWords = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < words.Count; index++)
        {
            if (!firstWords.TryGetValue(words[index].Text, out var positions)) firstWords.Add(words[index].Text, positions = []);
            positions.Add(index);
        }
        var candidates = new (int Start, int Count)?[turns.Count];
        for (var turnIndex = 0; turnIndex < turns.Count; turnIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var turn = turns[turnIndex];
            if (turn.Text.Length > MaximumTextCharacters || turn.TimingQuality == MeetingTimingQuality.Aligned) continue;
            var expected = LexicalWords(turn.Text);
            if (expected.Count == 0 || expected.Count > 512) continue;
            var match = firstWords.TryGetValue(expected[0].Text, out var positions)
                ? FindUnique(words, expected, 0, expected.Count, positions) : -1;
            if (match >= 0) candidates[turnIndex] = (match, expected.Count);
            if (match != -1) continue; // Ambiguous raw matches cannot be rescued by normalization.
            var expansion = EnglishNumberComparison.Expand(turn.Text);
            var expanded = expansion is null ? null : LexicalWords(expansion.Text);
            if (expanded is { Count: > 0 and <= 512 })
            {
                match = firstWords.TryGetValue(expanded[0].Text, out positions)
                    ? FindUnique(words, expanded, 0, expanded.Count, positions) : -1;
                if (match >= 0) candidates[turnIndex] = (match, expanded.Count);
                if (match != -1) continue;
            }
            var fallback = expected.Count >= 10 ? MatchEndpoints(words, expected, firstWords, cancellationToken) : null;
            if (fallback is { } rawSpan && !ContainsNumbers(words, rawSpan, expansion)) fallback = null;
            if (fallback is null && expanded is { Count: >= 10 and <= 512 })
            {
                fallback = MatchEndpoints(words, expanded, firstWords, cancellationToken);
                if (fallback is { } expandedSpan && !ContainsNumbers(words, expandedSpan, expansion)) fallback = null;
            }
            candidates[turnIndex] = fallback;
        }

        // Reusing the same audio words for two speaker turns would invent attribution.
        var owners = new int[words.Count];
        foreach (var turn in turns)
            if (turn.TimingQuality == MeetingTimingQuality.Aligned)
                for (var index = 0; index < words.Count; index++)
                    if (words[index].StartSample < turn.EndSeconds * SampleRate && words[index].EndSample > turn.StartSeconds * SampleRate)
                        owners[index]++;
        foreach (var candidate in candidates)
            if (candidate is { } span)
                for (var index = span.Start; index < span.Start + span.Count; index++) owners[index]++;
        var previousEnd = double.NegativeInfinity;
        var matchedNewTurn = false;
        for (var index = 0; index < turns.Count; index++)
        {
            if (turns[index].TimingQuality == MeetingTimingQuality.Aligned)
            {
                if (matchedNewTurn && turns[index].StartSeconds * SampleRate < previousEnd) return turns;
                previousEnd = Math.Max(previousEnd, turns[index].EndSeconds * SampleRate);
                continue;
            }
            if (candidates[index] is not { } span) continue;
            if (words[span.Start].StartSample < previousEnd) return turns;
            previousEnd = words[span.Start + span.Count - 1].EndSample;
            matchedNewTurn = true;
        }
        var result = turns.ToArray();
        for (var index = 0; index < result.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidates[index] is not { } span) continue;
            var exclusive = true;
            for (var word = span.Start; word < span.Start + span.Count; word++)
                if (owners[word] != 1) { exclusive = false; break; }
            if (!exclusive) continue;
            result[index] = result[index] with
            {
                StartSeconds = words[span.Start].StartSample / (double)SampleRate,
                EndSeconds = words[span.Start + span.Count - 1].EndSample / (double)SampleRate,
                TimingQuality = MeetingTimingQuality.Aligned
            };
        }
        return result;
    }

    private static int FindUnique(List<TimedWord> words, List<LexicalWord> expected, int start, int count, List<int> positions)
    {
        var match = -1;
        foreach (var position in positions)
        {
            if (position + count > words.Count) continue;
            var matches = true;
            for (var index = 0; index < count; index++)
                if (expected[start + index].Text != words[position + index].Text) { matches = false; break; }
            if (!matches) continue;
            if (match >= 0) return -2;
            match = position;
        }
        return match;
    }

    private static bool ContainsNumbers(List<TimedWord> words, (int Start, int Count) span, EnglishNumberComparison.Expansion? expansion)
    {
        if (expansion is null) return true;
        var cursor = span.Start;
        foreach (var number in expansion.Numbers)
        {
            var variants = new[] { LexicalWords(number.Original), LexicalWords(number.Spoken) };
            var found = false;
            for (var position = cursor; position < span.Start + span.Count && !found; position++)
                foreach (var variant in variants)
                {
                    if (position + variant.Count > span.Start + span.Count) continue;
                    var matches = true;
                    for (var index = 0; index < variant.Count; index++)
                        if (words[position + index].Text != variant[index].Text) { matches = false; break; }
                    if (!matches) continue;
                    cursor = position + variant.Count;
                    found = true;
                    break;
                }
            if (!found) return false;
        }
        return true;
    }

    private static (int Start, int Count)? MatchEndpoints(List<TimedWord> words, List<LexicalWord> expected,
        Dictionary<string, List<int>> positions, CancellationToken token)
    {
        const int anchorWords = 4;
        if (!positions.TryGetValue(expected[0].Text, out var starts) ||
            !positions.TryGetValue(expected[^anchorWords].Text, out var ends)) return null;
        var start = FindUnique(words, expected, 0, anchorWords, starts);
        var end = FindUnique(words, expected, expected.Count - anchorWords, anchorWords, ends);
        if (start < 0 || end < start + anchorWords) return null;
        var count = end + anchorWords - start;
        if (Math.Abs(count - expected.Count) > Math.Max(4, expected.Count * 0.2)) return null;
        var previous = new int[count + 1];
        var current = new int[count + 1];
        for (var row = 0; row < expected.Count; row++)
        {
            token.ThrowIfCancellationRequested();
            current[0] = 0;
            for (var column = 0; column < count; column++)
                current[column + 1] = expected[row].Text == words[start + column].Text
                    ? previous[column] + 1 : Math.Max(previous[column + 1], current[column]);
            (previous, current) = (current, previous);
        }
        if (previous[count] * 5 < Math.Max(expected.Count, count) * 4) return null;
        // Endpoint anchors bound the turn; no timestamps are inferred for unmatched interior words.
        return (start, count);
    }

    private static List<TimedWord>? ReadWords(IReadOnlyList<MeetingTimedToken> tokens, double duration)
    {
        var text = new StringBuilder();
        var offsets = new List<(int Start, int End, MeetingTimedToken Token)>(tokens.Count);
        long previousStart = -1;
        long previousEnd = -1;
        foreach (var token in tokens)
        {
            // Decoder padding can emit trailing punctuation after the captured clip.
            if (token is not null && token.Text is not null && token.StartSample >= duration * SampleRate &&
                token.Text.All(character => !char.IsLetterOrDigit(character))) continue;
            if (token is null || token.Text is null || token.StartSample < 0 || token.EndSample <= token.StartSample ||
                token.StartSample < previousStart || token.EndSample < previousEnd || token.EndSample > duration * SampleRate ||
                token.Text.Length > MaximumTextCharacters - text.Length)
                return null;
            var start = text.Length;
            text.Append(token.Text);
            offsets.Add((start, text.Length, token));
            previousStart = token.StartSample;
            previousEnd = token.EndSample;
        }
        var words = new List<TimedWord>();
        var tokenIndex = 0;
        var previousLastToken = -1;
        foreach (var word in LexicalWords(text.ToString()))
        {
            while (tokenIndex < offsets.Count && offsets[tokenIndex].End <= word.Start) tokenIndex++;
            if (tokenIndex == offsets.Count || tokenIndex <= previousLastToken) return null;
            var first = tokenIndex;
            while (tokenIndex < offsets.Count && offsets[tokenIndex].End < word.End) tokenIndex++;
            if (tokenIndex == offsets.Count) return null;
            words.Add(new TimedWord(word.Text, offsets[first].Token.StartSample, offsets[tokenIndex].Token.EndSample));
            previousLastToken = tokenIndex;
        }
        return words;
    }

    private static List<LexicalWord> LexicalWords(string text)
    {
        var result = new List<LexicalWord>();
        var word = new StringBuilder();
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && char.IsLetterOrDigit(text[index]))
            {
                if (word.Length == 0) start = index;
                word.Append(char.ToLowerInvariant(text[index]));
            }
            else if (index < text.Length && text[index] is '\'' or '’' && word.Length > 0)
            {
                continue;
            }
            else if (word.Length > 0)
            {
                result.Add(new LexicalWord(word.ToString(), start, index));
                word.Clear();
            }
        }
        return result;
    }

    private sealed record LexicalWord(string Text, int Start, int End);
    private sealed record TimedWord(string Text, long StartSample, long EndSample);
}
