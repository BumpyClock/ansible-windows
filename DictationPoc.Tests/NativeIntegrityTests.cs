using System.Security.Cryptography;
using DictationPoc.Core;

namespace DictationPoc.Tests;

[Trait("Category", "NativeIntegrity")]
public sealed class NativeIntegrityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DictationPoc-native-integrity-{Guid.NewGuid():N}");
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

    [Theory]
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

    private void WriteCatalog(params string[] entries) =>
        File.WriteAllText(CatalogPath, $$"""{"schema_version":1,"models":[{{string.Join(",", entries)}}]}""");

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
