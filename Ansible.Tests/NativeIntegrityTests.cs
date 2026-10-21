using System.Security.Cryptography;
using Ansible.Core;

namespace Ansible.Tests;

[Trait("Category", "NativeIntegrity")]
public sealed class NativeIntegrityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"Ansible-native-integrity-{Guid.NewGuid():N}");
    private static readonly byte[] Original = [1, 2, 3, 4];
    private string ModelPath => Path.Combine(_directory, "model.gguf");
    private string CatalogPath => Path.Combine(_directory, "catalog.json");

    public NativeIntegrityTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(ModelPath, Original);
    }

    [Fact]
    public async Task ModelIdsCanContainParameterSizeDecimalPoints()
    {
        WriteCatalog(EntryJson("model-0.6b"));
        var inventory = await NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None);
        Assert.Equal("model-0.6b", Assert.Single(inventory.Models).Id);
    }

    [Fact]
    public async Task MissingTaskDefaultsToAsr()
    {
        WriteCatalog(EntryJson());
        var inventory = await NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None);
        Assert.Equal("asr", Assert.Single(inventory.Models).Task);
        Assert.Equal("asr", inventory.Entries["test-model"].Task);
    }

    [Theory]
    [InlineData("moonshine_asr", "asr")]
    [InlineData("kroko_asr", "asr")]
    [InlineData("nemotron_3_diar", "diar")]
    public async Task CatalogTaskReachesTheConnectedModel(string family, string task)
    {
        WriteCatalog(EntryJson().Replace("\"family\":\"moonshine_asr\"",
            $"\"family\":\"{family}\",\"task\":\"{task}\"", StringComparison.Ordinal));
        var inventory = await NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None);
        var model = Assert.Single(inventory.Models);
        Assert.Equal(task, model.Task);
        Assert.Equal(task, inventory.Entries["test-model"].Task);
        Assert.Equal(family, model.Family);
        Assert.Equal(ModelPath, model.ModelPath);
    }

    [Theory]
    [InlineData("moonshine_asr", "\"translation\"")]
    [InlineData("moonshine_asr", "\"\"")]
    [InlineData("moonshine_asr", "\"ASR\"")]
    [InlineData("nemotron_3_diar", "\"DIAR\"")]
    [InlineData("NEMOTRON_3_DIAR", "\"diar\"")]
    [InlineData("moonshine_asr", "null")]
    [InlineData("moonshine_asr", "\"diar\"")]
    [InlineData("nemotron_asr", "\"diar\"")]
    [InlineData("kroko_asr", "\"diar\"")]
    [InlineData("vibevoice_asr_streaming", "\"diar\"")]
    [InlineData("nemotron_3_diar", "\"asr\"")]
    public async Task InvalidTasksAndTaskFamilyCombinationsAreRejected(string family, string taskJson)
    {
        WriteCatalog(EntryJson().Replace("\"family\":\"moonshine_asr\"",
            $"\"family\":\"{family}\",\"task\":{taskJson}", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None));
    }

    [Fact]
    public async Task DiarizationFamilyCannotOmitItsTask()
    {
        WriteCatalog(EntryJson().Replace("\"family\":\"moonshine_asr\"",
            "\"family\":\"nemotron_3_diar\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeModelCatalog.LoadAsync(CatalogPath));
    }

    [Fact]
    public async Task DefaultCatalogPinsTheNemotronRecognitionAndDiarizationPair()
    {
        var entries = await NativeModelCatalog.LoadAsync(RepositoryCatalogPath());

        Assert.Equal("nemotron-asr-0.6b-q8", AudioModelDefaults.RecognitionModelId);
        Assert.Equal("nemotron-3-diarization-q8", AudioModelDefaults.DiarizationModelId);
        var recognition = Assert.Single(entries, entry => entry.Id == AudioModelDefaults.RecognitionModelId);
        Assert.Equal("nemotron_asr", recognition.Family);
        Assert.Equal("asr", recognition.Task);
        Assert.Equal("streaming", recognition.Mode);
        Assert.Equal("live", recognition.Preview);
        Assert.False(recognition.SupportsCustomDictionary);

        var diarization = Assert.Single(entries, entry => entry.Id == AudioModelDefaults.DiarizationModelId);
        Assert.Equal("nemotron_3_diar", diarization.Family);
        Assert.Equal("diar", diarization.Task);
        Assert.Equal("streaming", diarization.Mode);
        Assert.Equal("final-only", diarization.Preview);
        Assert.Equal(106675136, diarization.Bytes);
        Assert.Equal("9a737455bd10123bcf1e036d9a0b07b6e8c42e7d0dd1a5ee4141dc386db46d0b", diarization.Sha256);
        Assert.Equal("audio-cpp/Nemotron-3-Diarization-GGUF", diarization.Repo);
        Assert.Equal("647d39feaa0e91dca5ce355a95403837b76dff56", diarization.Revision);
        Assert.Equal("nemotron-3-diarization-q8_0.gguf", diarization.RemoteFile);
        Assert.Equal("nemotron-3-diarization-q8_0.gguf", diarization.Filename);
        Assert.Equal("OpenMDW-1.1", diarization.License);
        Assert.False(diarization.SupportsCustomDictionary);
        Assert.Equal(
            "https://huggingface.co/audio-cpp/Nemotron-3-Diarization-GGUF/resolve/647d39feaa0e91dca5ce355a95403837b76dff56/nemotron-3-diarization-q8_0.gguf",
            diarization.DownloadUri.AbsoluteUri);
    }

    [Fact]
    public async Task CatalogPinsOptionalEnglishKrokoWithDictionarySupport()
    {
        var entries = await NativeModelCatalog.LoadAsync(RepositoryCatalogPath());
        Assert.Equal("kroko-en-64l-q8", AudioModelDefaults.KrokoModelId);
        var kroko = Assert.Single(entries, entry => entry.Id == AudioModelDefaults.KrokoModelId);
        Assert.Equal("kroko_asr", kroko.Family);
        Assert.Equal("asr", kroko.Task);
        Assert.Equal("streaming", kroko.Mode);
        Assert.Equal("live", kroko.Preview);
        Assert.True(kroko.SupportsCustomDictionary);
        Assert.Equal("English", kroko.Languages);
        Assert.Equal("Q8_0", kroko.Precision);
        Assert.Equal("audio-cpp/audio.cpp-gguf", kroko.Repo);
        Assert.Equal("351dbab8d8534675ee29440bb402e348b09e55e2", kroko.Revision);
        Assert.Equal("Kroko-ASR-GGUF/kroko-en-community-64-l-q8_0.gguf", kroko.RemoteFile);
        Assert.Equal("kroko-en-community-64-l-q8_0.gguf", kroko.Filename);
        Assert.Equal(167756928, kroko.Bytes);
        Assert.Equal("596c749fcb6c582f2d7aaa9aefcff2929e405867a4d04864a55b2c9c2963baa5", kroko.Sha256);
        Assert.Equal("CC-BY-SA", kroko.License);
        Assert.Equal(
            "https://huggingface.co/audio-cpp/audio.cpp-gguf/resolve/351dbab8d8534675ee29440bb402e348b09e55e2/Kroko-ASR-GGUF/kroko-en-community-64-l-q8_0.gguf",
            kroko.DownloadUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("kroko_asr")]
    [InlineData("qwen3_asr")]
    [InlineData("vibevoice_asr")]
    [InlineData("vibevoice_asr_streaming")]
    public async Task SupportedDictionaryCapabilityReachesTheConnectedModel(string family)
    {
        WriteCatalog(EntryJson().Replace("\"family\":\"moonshine_asr\"",
            $"\"family\":\"{family}\",\"supports_custom_dictionary\":true", StringComparison.Ordinal));
        var inventory = await NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None);
        Assert.True(Assert.Single(inventory.Models).SupportsCustomDictionary);
    }

    [Theory]
    [InlineData("moonshine_asr")]
    [InlineData("nemotron_asr")]
    [InlineData("unknown-family")]
    public async Task CatalogCannotClaimDictionarySupportForUnsupportedAdapters(string family)
    {
        WriteCatalog(EntryJson().Replace("\"family\":\"moonshine_asr\"",
            $"\"family\":\"{family}\",\"supports_custom_dictionary\":true", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None));
    }

    [Fact]
    public async Task DiscoveryRetainsHashButDoesNotPretendSameSizeWeightsAreVerified()
    {
        File.WriteAllBytes(ModelPath, [4, 3, 2, 1]);
        WriteCatalog(EntryJson());
        var inventory = await NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None);
        Assert.Single(inventory.Models);
        Assert.Equal(Hash, inventory.Entries["test-model"].Sha256);
        Assert.Throws<InvalidDataException>(() =>
            new NativeModelIntegrity().OpenVerified(ModelPath, Entry, CancellationToken.None));
    }

    [Fact]
    public void CachedVerificationIsInvalidatedBySameLengthCorruption()
    {
        var integrity = new NativeModelIntegrity();
        using (integrity.OpenVerified(ModelPath, Entry, CancellationToken.None)) { }
        var timestamp = File.GetLastWriteTimeUtc(ModelPath);
        File.WriteAllBytes(ModelPath, [4, 3, 2, 1]);
        File.SetLastWriteTimeUtc(ModelPath, timestamp.AddSeconds(2));
        var error = Assert.Throws<InvalidDataException>(() => integrity.OpenVerified(ModelPath, Entry, CancellationToken.None));
        Assert.Contains("SHA-256", error.Message);
    }

    [Fact]
    public void ReplacementWithPreservedLengthAndTimestampsStillNeedsHashVerification()
    {
        var integrity = new NativeModelIntegrity();
        using (integrity.OpenVerified(ModelPath, Entry, CancellationToken.None)) { }
        var written = File.GetLastWriteTimeUtc(ModelPath);
        var created = File.GetCreationTimeUtc(ModelPath);
        File.Move(ModelPath, ModelPath + ".old");
        File.WriteAllBytes(ModelPath, [4, 3, 2, 1]);
        File.SetCreationTimeUtc(ModelPath, created);
        File.SetLastWriteTimeUtc(ModelPath, written);
        Assert.Throws<InvalidDataException>(() => integrity.OpenVerified(ModelPath, Entry, CancellationToken.None));
    }

    [Fact]
    public void VerifiedLeasePreventsWriteOrReplacementDuringNativeParsing()
    {
        using var lease = new NativeModelIntegrity().OpenVerified(ModelPath, Entry, CancellationToken.None);
        Assert.Equal(4, lease.Length);
        Assert.Throws<IOException>(() => File.WriteAllBytes(ModelPath, [4, 3, 2, 1]));
        Assert.Throws<IOException>(() => File.Move(ModelPath, ModelPath + ".replaced"));
    }

    [Fact]
    public void CancellationNeverCachesAnUnverifiedFile()
    {
        var integrity = new NativeModelIntegrity();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => integrity.OpenVerified(ModelPath, Entry, cancellation.Token));
        File.WriteAllBytes(ModelPath, [4, 3, 2, 1]);
        Assert.Throws<InvalidDataException>(() => integrity.OpenVerified(ModelPath, Entry, CancellationToken.None));
    }

    [Theory]
    [InlineData("filename", "\"..\\\\model.gguf\"")]
    [InlineData("filename", "\"C:\\\\model.gguf\"")]
    [InlineData("filename", "\"model.gguf:alternate\"")]
    [InlineData("filename", "\"model.gguf.\"")]
    [InlineData("filename", "\"CON.gguf\"")]
    [InlineData("filename", "\"\"")]
    [InlineData("filename", "null")]
    [InlineData("sha256", "\"bad hash\"")]
    [InlineData("sha256", "null")]
    [InlineData("id", "null")]
    [InlineData("family", "\"\"")]
    [InlineData("mode", "\"live\"")]
    [InlineData("bytes", "0")]
    public async Task MalformedEntriesAreRejectedBeforeDiscovery(string property, string value)
    {
        var originalValue = property switch
        {
            "filename" => "\"model.gguf\"",
            "sha256" => $"\"{Hash}\"",
            "id" => "\"test-model\"",
            "family" => "\"moonshine_asr\"",
            "mode" => "\"streaming\"",
            "bytes" => "4",
            _ => throw new ArgumentOutOfRangeException(nameof(property))
        };
        WriteCatalog(EntryJson().Replace($"\"{property}\":{originalValue}", $"\"{property}\":{value}", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None));
    }

    [Theory]
    [InlineData("TEST-MODEL", "another.gguf")]
    [InlineData("another-model", "MODEL.GGUF")]
    public async Task DuplicateIdsOrWindowsFilenamesAreRejected(string id, string filename)
    {
        WriteCatalog(EntryJson(), EntryJson(id, filename));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateJsonPropertiesAreRejected()
    {
        WriteCatalog(EntryJson().Replace("\"bytes\":4", "\"bytes\":4,\"bytes\":4", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None));
    }

    [Fact]
    public async Task NullAndMissingRequiredEntriesAreRejected()
    {
        WriteCatalog("null");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None));
        WriteCatalog("{\"id\":\"test-model\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeModelCatalog.ReadAsync(CatalogPath, _directory, CancellationToken.None));
    }

    private static string Hash => Convert.ToHexString(SHA256.HashData(Original)).ToLowerInvariant();
    private static NativeModelEntry Entry => new()
    {
        Id = "test-model", Family = "moonshine_asr", Mode = "streaming",
        Filename = "model.gguf", Bytes = 4, Sha256 = Hash
    };

    private static string EntryJson(string id = "test-model", string filename = "model.gguf") => $$"""
        {"id":"{{id}}","family":"moonshine_asr","mode":"streaming","filename":"{{filename}}","bytes":4,"sha256":"{{Hash}}"}
        """;

    private static string RepositoryCatalogPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "tools", "audio-models.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tools", "audio-models.json");
    }

    private void WriteCatalog(params string[] entries) =>
        File.WriteAllText(CatalogPath, $$"""{"schema_version":1,"models":[{{string.Join(",", entries)}}]}""");

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
