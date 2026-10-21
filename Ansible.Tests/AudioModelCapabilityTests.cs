using Ansible.Core;

namespace Ansible.Tests;

public sealed class AudioModelCapabilityTests
{
    private static AudioModel Model(string family, string mode, string preview) =>
        new() { Id = $"{family}-{mode}-{preview}", Family = family, Mode = mode, Preview = preview };

    [Fact]
    public void StreamingPlainSpeechModelsSupportFinalInsertionRegardlessOfPreview()
    {
        // Preview timing does not govern final insertion.
        Assert.True(Model("nemotron_asr", "streaming", "live").CanInsertDictation);

        // Buffered and final-only models provide the same final insertion contract.
        Assert.True(Model("moonshine_asr", "streaming", "final-only").CanInsertDictation);
        Assert.True(Model("qwen3_asr", "streaming", "buffered").CanInsertDictation);
        Assert.True(Model("kroko_asr", "streaming", "live").CanInsertDictation);

        Assert.True(Model("vibevoice_asr_streaming", "streaming", "live").CanInsertDictation);

        Assert.True(Model("vibevoice_asr", "offline", "final-only").CanInsertDictation);
        Assert.False(Model("unknown", "streaming", "live").CanInsertDictation);
        Assert.False(Model("moonshine_asr", "offline", "final-only").CanInsertDictation);
    }

    [Fact]
    public void AnnotatedVibeVoiceSupportsInsertionWithoutBeingPlainSpeech()
    {
        var vibevoice = Model("vibevoice_asr_streaming", "streaming", "live");
        Assert.Equal("live", vibevoice.Preview);
        Assert.False(vibevoice.HasPlainSpeechText);
        Assert.True(vibevoice.CanInsertDictation);
    }

    [Theory]
    [InlineData("nemotron_3_diar", "streaming")]
    [InlineData("nemotron_asr", "streaming")]
    [InlineData("kroko_asr", "streaming")]
    [InlineData("vibevoice_asr", "offline")]
    public void DiarizationCannotBeUsedForMicrophoneDictation(string family, string mode)
    {
        var model = Model(family, mode, "final-only") with { Task = "diar" };
        Assert.False(model.SupportsMicrophone);
        Assert.False(model.CanInsertDictation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("asr")]
    public void MissingOrExplicitAsrTaskRetainsDictationSupport(string? task)
    {
        var model = Model("nemotron_asr", "streaming", "live") with { Task = task };
        Assert.True(model.SupportsMicrophone);
        Assert.True(model.CanInsertDictation);
    }

    [Theory]
    [InlineData("moonshine_asr", true)]
    [InlineData("qwen3_asr", true)]
    [InlineData("nemotron_asr", true)]
    [InlineData("kroko_asr", true)]
    [InlineData("vibevoice_asr_streaming", false)]
    [InlineData("vibevoice_asr", false)]
    [InlineData(null, false)]
    public void PlainSpeechFamilyClassificationIsSharedWithNormalization(string? family, bool expected) =>
        Assert.Equal(expected, SpeechContent.IsPlainSpeechFamily(family));
}
