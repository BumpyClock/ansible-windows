using Ansible.Core;

namespace Ansible.Tests;

public sealed class SpeechExtractionTests
{
    private static ModelOutput Output(string text, string[]? segments = null, string[]? turns = null) =>
        new(text, segments ?? [], turns ?? []);

    [Fact]
    public void PlainFamiliesReadTheWholeTextAsSpeechWithoutStripping()
    {
        var result = SpeechExtraction.ToResult("moonshine_asr", Output("Speaker 0: is what I said."));
        Assert.Equal("Speaker 0: is what I said.", result.DisplayText);
        Assert.Equal("Speaker 0: is what I said.", result.SpeechText);
        Assert.Equal(6, result.SpokenWords);
    }

    [Fact]
    public void SpeakerLabelledFamilyInsertsTextWithoutDiarizationLabels()
    {
        var result = SpeechExtraction.ToResult("vibevoice_asr_streaming",
            Output("Speaker 0: Some call me nature. Speaker 1: Others call me Mother Nature."));
        Assert.Equal("Speaker 0: Some call me nature. Speaker 1: Others call me Mother Nature.", result.DisplayText);
        Assert.Equal("Some call me nature. Others call me Mother Nature.", result.SpeechText);
        Assert.Equal(9, result.SpokenWords);
    }

    [Fact]
    public void SpeakerLabelledFamilyIgnoresNativeTurnsSoLiveAndFileResultsAgree()
    {
        var result = SpeechExtraction.ToResult("vibevoice_asr_streaming",
            Output("Speaker 0: hello. Speaker 1: world.", turns: ["hello.", "world."]));
        Assert.Equal("hello. world.", result.SpeechText);
    }

    [Fact]
    public void SpeakerLabelledOutputWithOnlyLabelsIsEmptySpeechNotUnknown()
    {
        var result = SpeechExtraction.ToResult("vibevoice_asr_streaming", Output("Speaker 0:"));
        Assert.Equal("", result.SpeechText);
        Assert.Equal(0, result.SpokenWords);
    }

    [Theory]
    [InlineData("Speaker 0: hello", "hello")]
    [InlineData("Speaker0:hello", "hello")]
    [InlineData("Speaker 12 : hello", "hello")]
    [InlineData("Speaker  3:\n hello", "hello")]
    [InlineData("intro Speaker 1: hello", "intro hello")]
    [InlineData("Speaker 0: I said Speaker 0.", "I said Speaker 0.")]
    [InlineData("speaker 0: lower case is speech", "speaker 0: lower case is speech")]
    [InlineData("Speaker: no digits", "Speaker: no digits")]
    [InlineData("Speaker 0 no colon", "Speaker 0 no colon")]
    [InlineData("Speaker A: letters", "Speaker A: letters")]
    [InlineData("no labels at all", "no labels at all")]
    [InlineData("", "")]
    public void StripSpeakerLabelsMatchesUpstreamLabelGrammar(string text, string expected) =>
        Assert.Equal(expected, SpeechExtraction.StripSpeakerLabels(text));

    [Fact]
    public void AnnotatedFamilyReadsSpeechOnlyFromCompleteMetadata()
    {
        var segments = SpeechExtraction.ToResult("vibevoice_asr",
            Output("Speaker 0: I said Speaker 0.", segments: ["I said Speaker 0."], turns: ["I said Speaker 0."]));
        Assert.Equal("I said Speaker 0.", segments.SpeechText);
        Assert.Equal(4, segments.SpokenWords);

        var turnsOnly = SpeechExtraction.ToResult("vibevoice_asr",
            Output("Speaker 0: hello. Speaker 1: world.", turns: ["hello.", "world."]));
        Assert.Equal("hello. world.", turnsOnly.SpeechText);

        var absent = SpeechExtraction.ToResult("vibevoice_asr", Output("Speaker 0:"));
        var incomplete = SpeechExtraction.ToResult("vibevoice_asr", Output("Speaker 0: hello", segments: ["hello", ""]));
        Assert.Null(absent.SpeechText);
        Assert.Null(absent.SpokenWords);
        Assert.Null(incomplete.SpeechText);
        Assert.Null(incomplete.SpokenWords);
    }

    [Fact]
    public void UnknownFamilyNeverYieldsSpeech()
    {
        var result = SpeechExtraction.ToResult("future_asr", Output("hello", segments: ["hello"], turns: ["hello"]));
        Assert.Equal("hello", result.DisplayText);
        Assert.Null(result.SpeechText);
        Assert.False(SpeechExtraction.IsSupported("future_asr"));
        Assert.False(SpeechExtraction.IsSupported(null));
    }
}
