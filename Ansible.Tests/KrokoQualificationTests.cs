using System.Text.Json;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class KrokoQualificationTests
{
    [Fact]
    public async Task RepositoryCatalogOffersPinnedEnglishKrokoWithHotwords()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools", "audio-models.json")))
            root = root.Parent;
        Assert.NotNull(root);
        var entries = await NativeModelCatalog.LoadAsync(Path.Combine(root.FullName, "tools", "audio-models.json"));
        var entry = Assert.Single(entries, model => model.Id == Kroko.Id);
        Assert.Equal("kroko_asr", entry.Family);
        Assert.Equal("streaming", entry.Mode);
        Assert.Equal("live", entry.Preview);
        Assert.True(entry.SupportsCustomDictionary);
        Assert.Equal("English", entry.Languages);
        Assert.Equal("351dbab8d8534675ee29440bb402e348b09e55e2", entry.Revision);
        Assert.Equal("kroko-en-community-64-l-q8_0.gguf", entry.Filename);
        Assert.Equal(167756928, entry.Bytes);
        Assert.Equal("596c749fcb6c582f2d7aaa9aefcff2929e405867a4d04864a55b2c9c2963baa5", entry.Sha256);
    }

    [Fact]
    public async Task SlashDictionaryPhraseIsRejectedBeforeNativeWork()
    {
        await using var engine = new NativeAudioEngine("missing.dll", "missing.json", "models");
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.TranscribeAsync(
            Kroko, new([0f], 16000, 1),
            new(CustomDictionary: "and/or", Backend: NativeBackend.Cpu), null, CancellationToken.None));
    }

    private static readonly AudioModel Kroko = new()
    {
        Id = "kroko-en-64l-q8", Family = "kroko_asr", Mode = "streaming", SupportsCustomDictionary = true
    };

    private static readonly DeviceBenchmarkRequest Request = new(
        "native.dll", "catalog.json", "models", "public.wav", Kroko.Id,
        NativeBackend.Cpu, 0, 4, 512, "streaming", 0);

    [Fact]
    public void HotwordQualificationUsesPublicHintsInsteadOfPrivateVocabulary()
    {
        var options = DeviceBenchmarkWorker.CreateOptions(Request with { UseDictionary = true }, Kroko);

        Assert.Equal("Mother Nature", options.CustomDictionary);
        Assert.Empty(DeviceBenchmarkWorker.CreateOptions(Request, Kroko).CustomDictionary);
    }

    [Theory]
    [InlineData("nemotron_asr", false)]
    [InlineData("vibevoice_asr_streaming", true)]
    [InlineData("kroko_asr", false)]
    public void UnsupportedHotwordQualificationFailsExplicitly(string family, bool supportsDictionary)
    {
        Assert.Throws<InvalidDataException>(() => DeviceBenchmarkWorker.CreateOptions(
            Request with { UseDictionary = true },
            Kroko with { Family = family, SupportsCustomDictionary = supportsDictionary }));
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
