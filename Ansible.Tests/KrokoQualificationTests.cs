using System.Text.Json;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class KrokoQualificationTests
{
    private static readonly AudioModel Kroko = new()
    {
        Id = AudioModelDefaults.KrokoModelId, Family = "kroko_asr", Task = "asr", Mode = "streaming",
        SupportsCustomDictionary = true
    };
    private static readonly DeviceBenchmarkRequest Request = new("native.dll", "catalog.json", "models", "public.wav",
        AudioModelDefaults.KrokoModelId, NativeBackend.Cpu, 0, 4, 512, "streaming", 4096);

    [Fact]
    public void HotwordQualificationUsesPublicHintsInsteadOfPrivateVocabulary()
    {
        var options = DeviceBenchmarkWorker.CreateOptions(Request with { UseDictionary = true }, Kroko);
        Assert.Equal("Mother Nature", options.CustomDictionary);
        Assert.Equal(NativeBackend.Cpu, options.Backend);
        Assert.Empty(DeviceBenchmarkWorker.CreateOptions(Request, Kroko).CustomDictionary);
    }

    [Theory]
    [InlineData("nemotron_asr", false)]
    [InlineData("vibevoice_asr_streaming", true)]
    [InlineData("kroko_asr", false)]
    public void UnsupportedHotwordQualificationFailsExplicitly(string family, bool supportsDictionary)
    {
        Assert.Throws<InvalidDataException>(() => DeviceBenchmarkWorker.CreateOptions(
            Request with { UseDictionary = true }, Kroko with { Family = family, SupportsCustomDictionary = supportsDictionary }));
    }

    [Fact]
    public void WorkerProtocolRetainsDecoderModeWithoutCarryingDictionaryText()
    {
        var encoded = JsonSerializer.Serialize(Request with { UseDictionary = true }, DeviceBenchmarkJson.Default.DeviceBenchmarkRequest);
        var decoded = JsonSerializer.Deserialize(encoded, DeviceBenchmarkJson.Default.DeviceBenchmarkRequest);
        Assert.NotNull(decoded);
        Assert.True(decoded.UseDictionary);
        Assert.DoesNotContain("Mother Nature", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomDictionary", encoded, StringComparison.Ordinal);
    }
}
