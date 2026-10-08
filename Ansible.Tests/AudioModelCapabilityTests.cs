using Ansible.Core;

namespace Ansible.Tests;

public sealed class AudioModelCapabilityTests
{
    private static AudioModel Model(string? family, string mode, string preview) =>
        new() { Id = $"{family}-{mode}-{preview}", Family = family, Mode = mode, Preview = preview };

    [Fact]
    public void StreamingModelsWithAReadableOutputLayoutSupportFinalInsertionRegardlessOfPreview()
    {
        // Preview timing does not govern final insertion.
        Assert.True(Model("nemotron_asr", "streaming", "live").CanInsertDictation);

        // Buffered and final-only models provide the same final insertion contract.
        Assert.True(Model("moonshine_asr", "streaming", "final-only").CanInsertDictation);
        Assert.True(Model("qwen3_asr", "streaming", "buffered").CanInsertDictation);

        // Diarized output is inserted after its speaker labels are removed.
        Assert.True(Model("vibevoice_asr_streaming", "streaming", "live").CanInsertDictation);

        // Offline models and families without a reading rule are excluded regardless of preview.
        Assert.False(Model("vibevoice_asr", "offline", "final-only").CanInsertDictation);
        Assert.False(Model("future_asr", "streaming", "live").CanInsertDictation);
        Assert.False(Model(null, "streaming", "live").CanInsertDictation);
    }

    [Theory]
    [InlineData("moonshine_asr", SpeechLayout.Plain)]
    [InlineData("qwen3_asr", SpeechLayout.Plain)]
    [InlineData("nemotron_asr", SpeechLayout.Plain)]
    [InlineData("vibevoice_asr_streaming", SpeechLayout.SpeakerLabelled)]
    [InlineData("vibevoice_asr", SpeechLayout.Annotated)]
    [InlineData("future_asr", SpeechLayout.Unknown)]
    [InlineData(null, SpeechLayout.Unknown)]
    public void EveryCatalogFamilyHasOneOutputLayout(string? family, SpeechLayout expected) =>
        Assert.Equal(expected, SpeechExtraction.LayoutOf(family));
}
