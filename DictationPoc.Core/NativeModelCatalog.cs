using System.Text.Json;
using System.Text.Json.Serialization;

namespace DictationPoc.Core;

internal sealed record NativeModelCatalog
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }
    [JsonPropertyName("models")]
    public required List<NativeModelEntry> Models { get; init; }

    public static async Task<IReadOnlyList<AudioModel>> ReadAsync(
        string catalogPath, string directory, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(catalogPath);
        var catalog = await JsonSerializer.DeserializeAsync(
            stream, NativeModelJsonContext.Default.NativeModelCatalog, cancellationToken);
        if (catalog is null || catalog.SchemaVersion != 1 || catalog.Models is null)
        {
            throw new InvalidDataException("The native model catalog is invalid.");
        }
        List<AudioModel> models = [];
        foreach (var entry in catalog.Models)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Family) ||
                entry.Filename != Path.GetFileName(entry.Filename) || entry.Bytes <= 0 ||
                entry.Mode is not "offline" and not "streaming")
            {
                throw new InvalidDataException("The native model catalog contains an invalid entry.");
            }
            var path = Path.Combine(directory, entry.Filename);
            if (!File.Exists(path))
            {
                continue;
            }
            if (new FileInfo(path).Length != entry.Bytes)
            {
                throw new InvalidDataException($"Model '{entry.Id}' has an unexpected size. Re-run the verified model setup.");
            }
            models.Add(new AudioModel
            {
                Id = entry.Id, Family = entry.Family, Mode = entry.Mode, Task = "asr",
                ModelPath = path, Preview = entry.Preview, DisplayName = entry.DisplayName
            });
        }
        if (models.Count == 0)
        {
            throw new FileNotFoundException("No verified models are installed in this folder. Run tools\\Setup-AudioBackend.ps1.");
        }
        return models;
    }
}

internal sealed record NativeModelEntry
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("family")] public required string Family { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("filename")] public required string Filename { get; init; }
    [JsonPropertyName("bytes")] public required long Bytes { get; init; }
    [JsonPropertyName("preview")] public string? Preview { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
}

[JsonSerializable(typeof(NativeModelCatalog))]
internal partial class NativeModelJsonContext : JsonSerializerContext;
