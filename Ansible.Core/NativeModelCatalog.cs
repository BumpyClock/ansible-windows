using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansible.Core;

public sealed record NativeModelCatalog
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }
    [JsonPropertyName("models")]
    public required List<NativeModelEntry> Models { get; init; }

    public static async Task<IReadOnlyList<NativeModelEntry>> LoadAsync(
        string catalogPath, CancellationToken cancellationToken = default)
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
        ValidateEntries(catalog.Models);
        return catalog.Models.AsReadOnly();
    }

    internal static void ValidateEntries(IReadOnlyList<NativeModelEntry> models)
    {
        var entries = new Dictionary<string, NativeModelEntry>(StringComparer.OrdinalIgnoreCase);
        var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in models)
        {
            if (entry is null || !IsIdentifier(entry.Id) || !IsIdentifier(entry.Family) ||
                !IsFilename(entry.Filename) || entry.Bytes <= 0 ||
                entry.Mode is not "offline" and not "streaming" ||
                entry.Preview is not (null or "final-only" or "live" or "buffered") ||
                entry.SupportsCustomDictionary &&
                    entry.Family is not ("qwen3_asr" or "vibevoice_asr" or "vibevoice_asr_streaming") ||
                entry.Sha256 is null || entry.Sha256.Length != 64 || !entry.Sha256.All(char.IsAsciiHexDigit) ||
                string.Equals(entry.Filename, ".localvoice-models.lock", StringComparison.OrdinalIgnoreCase) ||
                !entries.TryAdd(entry.Id, entry) || !filenames.Add(entry.Filename) ||
                !filenames.Add(entry.Filename + ".partial") || !filenames.Add(entry.Filename + ".partial.json"))
            {
                throw new InvalidDataException("The native model catalog contains an invalid entry.");
            }
            if (entry.Repo is not null || entry.Revision is not null || entry.RemoteFile is not null)
                _ = entry.DownloadUri;
        }
    }

    internal static async Task<NativeModelInventory> ReadAsync(
        string catalogPath, string directory, CancellationToken cancellationToken)
    {
        var definitions = await LoadAsync(catalogPath, cancellationToken);
        directory = Path.GetFullPath(directory);
        List<AudioModel> models = [];
        foreach (var entry in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, entry.Filename);
            if (!File.Exists(path))
            {
                continue;
            }
            if (new FileInfo(path).Length != entry.Bytes)
            {
                continue;
            }
            models.Add(new AudioModel
            {
                Id = entry.Id, Family = entry.Family, Mode = entry.Mode, Task = "asr",
                ModelPath = path, Preview = entry.Preview, DisplayName = entry.DisplayName,
                SupportsCustomDictionary = entry.SupportsCustomDictionary
            });
        }
        return new NativeModelInventory(models.AsReadOnly(),
            definitions.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase));
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

public sealed record NativeModelEntry
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("family")] public required string Family { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("filename")] public required string Filename { get; init; }
    [JsonPropertyName("bytes")] public required long Bytes { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("preview")] public string? Preview { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("repo")] public string? Repo { get; init; }
    [JsonPropertyName("revision")] public string? Revision { get; init; }
    [JsonPropertyName("remote_file")] public string? RemoteFile { get; init; }
    [JsonPropertyName("license")] public string? License { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("languages")] public string? Languages { get; init; }
    [JsonPropertyName("precision")] public string? Precision { get; init; }
    [JsonPropertyName("license_notes")] public string? LicenseNotes { get; init; }
    [JsonPropertyName("supports_custom_dictionary")] public bool SupportsCustomDictionary { get; init; }

    [JsonIgnore]
    public long EstimatedMemoryBytes => NativeMemory.EstimateRequired(
        Bytes, Mode == "streaming" ? NativeMemory.MaximumStreamFrames * sizeof(float) : 0, 512);

    [JsonIgnore]
    public Uri DownloadUri
    {
        get
        {
            if (Repo is null || Repo.Split('/') is not [var owner, var repository] ||
                !SafeSegment(owner) || !SafeSegment(repository) ||
                Revision is null || Revision.Length != 40 || !Revision.All(char.IsAsciiHexDigit) ||
                RemoteFile is null || !RemoteFile.Split('/').All(SafeSegment))
                throw new InvalidDataException($"Model '{Id}' has no valid pinned download location.");
            return new Uri($"https://huggingface.co/{Repo}/resolve/{Revision}/{string.Join("/", RemoteFile.Split('/').Select(Uri.EscapeDataString))}");
        }
    }

    private static bool SafeSegment(string value) => value.Length > 0 && value is not "." and not ".." &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

[JsonSourceGenerationOptions(AllowDuplicateProperties = false)]
[JsonSerializable(typeof(NativeModelCatalog))]
internal partial class NativeModelJsonContext : JsonSerializerContext;
