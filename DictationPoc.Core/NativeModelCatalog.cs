using System.Text.Json;
using System.Text.Json.Serialization;

namespace DictationPoc.Core;

internal sealed record NativeModelCatalog
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }
    [JsonPropertyName("models")]
    public required List<NativeModelEntry> Models { get; init; }

    public static async Task<NativeModelInventory> ReadAsync(
        string catalogPath, string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(catalogPath);
        if (stream.Length > 1024 * 1024)
            throw new InvalidDataException("The native model catalog exceeds the supported size.");
        NativeModelCatalog? catalog;
        try
        {
            catalog = await JsonSerializer.DeserializeAsync(
                stream, NativeModelJsonContext.Default.NativeModelCatalog, cancellationToken);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The native model catalog is malformed.", error);
        }
        if (catalog is null || catalog.SchemaVersion != 1 || catalog.Models is null ||
            catalog.Models.Count is 0 or > 256)
        {
            throw new InvalidDataException("The native model catalog is invalid.");
        }
        directory = Path.GetFullPath(directory);
        List<AudioModel> models = [];
        var entries = new Dictionary<string, NativeModelEntry>(StringComparer.OrdinalIgnoreCase);
        var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || !IsIdentifier(entry.Id) || !IsIdentifier(entry.Family) ||
                !IsFilename(entry.Filename) || entry.Bytes <= 0 ||
                entry.Mode is not "offline" and not "streaming" ||
                entry.Preview is not (null or "final-only" or "live" or "buffered") ||
                entry.Sha256 is null || entry.Sha256.Length != 64 || !entry.Sha256.All(char.IsAsciiHexDigit) ||
                !entries.TryAdd(entry.Id, entry) || !filenames.Add(entry.Filename))
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
            throw new FileNotFoundException("No models are installed in this folder. Run tools\\Setup-AudioBackend.ps1.");
        }
        return new NativeModelInventory(models.AsReadOnly(), entries);
    }

    private static bool IsIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsFilename(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value != Path.GetFileName(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.EndsWith('.') || value.EndsWith(' '))
            return false;
        var stem = value.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
    }
}

internal sealed record NativeModelInventory(
    IReadOnlyList<AudioModel> Models, IReadOnlyDictionary<string, NativeModelEntry> Entries);

internal sealed record NativeModelEntry
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("family")] public required string Family { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("filename")] public required string Filename { get; init; }
    [JsonPropertyName("bytes")] public required long Bytes { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("preview")] public string? Preview { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
}

[JsonSourceGenerationOptions(AllowDuplicateProperties = false)]
[JsonSerializable(typeof(NativeModelCatalog))]
internal partial class NativeModelJsonContext : JsonSerializerContext;
