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

        // A "live" display preview does not qualify a family that carries no authoritative speech content.
        Assert.False(Model("vibevoice_asr_streaming", "streaming", "live").CanInsertDictation);

        // Offline/annotated families are excluded regardless of preview.
        Assert.False(Model("vibevoice_asr", "offline", "final-only").CanInsertDictation);
    }

    [Fact]
    public void LivePreviewLabelAloneDoesNotEstablishAuthoritativeSpeech()
    {
        var vibevoice = Model("vibevoice_asr_streaming", "streaming", "live");
        Assert.Equal("live", vibevoice.Preview);
        Assert.False(vibevoice.HasPlainSpeechText);
        Assert.False(vibevoice.CanInsertDictation);
    }

    [Theory]
    [InlineData("moonshine_asr", true)]
    [InlineData("qwen3_asr", true)]
    [InlineData("nemotron_asr", true)]
    [InlineData("vibevoice_asr_streaming", false)]
    [InlineData("vibevoice_asr", false)]
    [InlineData(null, false)]
    public void PlainSpeechFamilyClassificationIsSharedWithNormalization(string? family, bool expected) =>
        Assert.Equal(expected, SpeechContent.IsPlainSpeechFamily(family));
}
