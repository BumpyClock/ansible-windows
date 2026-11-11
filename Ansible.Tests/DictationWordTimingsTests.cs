using Ansible.Core;

namespace Ansible.Tests;

public sealed class DictationWordTimingsTests
{
    [Fact]
    public void MapsSubwordsToUnchangedSpeechOffsetsAndIgnoresPaddingPunctuation()
    {
        const string speech = "  Click, don't click THIS!  ";
        MeetingTimedToken[] tokens = [new(" Click", 0, 1280), new(", don", 1280, 2560),
            new("'t", 2560, 3840), new(" click", 3840, 5120), new(" this", 5120, 6400), new("!", 16000, 17280)];
        var mapped = MeetingWordAlignment.MapSpeechWords(speech, tokens, 16000, SpeechWordTimingSource.NativeTokenEmission);
        Assert.NotNull(mapped);
        Assert.Equal(["Click", "don't", "click", "THIS"], mapped.Words.Select(word => speech.Substring(word.TextStart, word.TextLength)));
        Assert.Equal(new SpeechWordTiming(9, 5, 1280, 3840), mapped.Words[1]);
        Assert.Equal(SpeechWordTimingSource.NativeTokenEmission, mapped.Source);
    }

    [Theory]
    [InlineData("click here", " click there", 0, 1280)]
    [InlineData("click", " click", -1, 1280)]
    [InlineData("click", " click", 1280, 1280)]
    [InlineData("click", " click", 0, 16001)]
    public void AbstainsForDisagreementOrInvalidNativeTimes(string speech, string piece, long start, long end)
    {
        Assert.Null(MeetingWordAlignment.MapSpeechWords(speech, [new(piece, start, end)], 16000,
            SpeechWordTimingSource.NativeTokenEmission));
    }

    [Fact]
    public void CumulativeRevisableTailReplacesEarlierSnapshot()
    {
        var first = MeetingWordAlignment.MapSpeechWords("draw now", [new("draw", 0, 1280), new(" now", 1280, 2560)],
            16000, SpeechWordTimingSource.NativeTokenEmission)!;
        var revised = MeetingWordAlignment.MapSpeechWords("draw now", [new("draw", 0, 1280), new(" now", 1280, 3840)],
            16000, SpeechWordTimingSource.NativeTokenEmission)!;
        Assert.Equal(2560, first.Words[1].EndSample);
        Assert.Equal(3840, revised.Words[1].EndSample);
        Assert.Equal(2, revised.Words.Count);
    }

    [Fact]
    public void SupportsFiveMinutesWithoutExtendingNativeBounds()
    {
        var mapped = MeetingWordAlignment.MapSpeechWords("last word", [new("last", 4700000, 4720000), new(" word", 4720000, 4800000)],
            4800000, SpeechWordTimingSource.NativeWordSpan);
        Assert.Equal(4800000, mapped!.Words[^1].EndSample);
        Assert.Null(MeetingWordAlignment.MapSpeechWords("last word", [], 4800001, SpeechWordTimingSource.NativeWordSpan));
    }

    [Fact]
    public void MeetingMappingKeepsMoreThanFiveMinutesAnd8192NativeWords()
    {
        var tokens = Enumerable.Range(0, 8193).Select(index => new MeetingTimedToken(index == 0 ? "word" : " word",
            index * 100, index * 100 + 50)).ToArray();
        var speech = string.Join(" ", Enumerable.Repeat("word", tokens.Length));
        var processed = 16000L * 5 * 60 + 1;

        Assert.Null(MeetingWordAlignment.MapSpeechWords(speech, tokens, processed, SpeechWordTimingSource.NativeWordSpan));
        var mapped = MeetingWordAlignment.MapMeetingSpeechWords(speech, tokens, processed, SpeechWordTimingSource.NativeWordSpan);
        Assert.Equal(tokens.Length, mapped!.Words.Count);
        Assert.Equal(tokens[^1].EndSample, mapped.Words[^1].EndSample);
    }

    [Fact]
    public void ReorderedAndUnmatchedWordsReceiveNoTimes()
    {
        Assert.Null(MeetingWordAlignment.MapSpeechWords("second first", [new("first", 0, 1280), new(" second", 1280, 2560)],
            16000, SpeechWordTimingSource.NativeWordSpan));
        Assert.Null(MeetingWordAlignment.MapSpeechWords("first extra second", [new("first", 0, 1280), new(" second", 1280, 2560)],
            16000, SpeechWordTimingSource.NativeWordSpan));
    }
}
