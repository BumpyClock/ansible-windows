using System.Text.Json;
using System.Text.Json.Serialization;

namespace DictationPoc;

internal sealed record AppPaths(
    string NativeLibrary, string ModelCatalog, string ModelsDirectory,
    string Usage, string StartupLog, string PublicSample, string ModelPreferences)
{
    public static AppPaths Create(string baseDirectory, string localDataDirectory)
    {
        var settingsPath = Path.Combine(baseDirectory, "runtime-settings.json");
        var preferencesPath = Path.Combine(localDataDirectory, "model-settings.json");
        RuntimeSettings? settings = null;
        if (File.Exists(preferencesPath))
        {
            settings = JsonSerializer.Deserialize(File.ReadAllText(preferencesPath), RuntimeSettingsContext.Default.RuntimeSettings)
                ?? throw new InvalidDataException("Model settings are empty.");
        }
        else if (File.Exists(settingsPath))
        {
            settings = JsonSerializer.Deserialize(File.ReadAllText(settingsPath), RuntimeSettingsContext.Default.RuntimeSettings)
                ?? throw new InvalidDataException("Deployment settings are empty.");
        }
        var modelDirectory = settings?.ModelsDirectory is { Length: > 0 } configured
            ? Path.GetFullPath(configured, baseDirectory)
            : Path.Combine(localDataDirectory, "models");
        return new(
            Path.Combine(baseDirectory, "audiocpp.dll"),
            Path.Combine(baseDirectory, "audio-models.json"),
            modelDirectory,
            Path.Combine(localDataDirectory, "usage.json"),
            Path.Combine(localDataDirectory, "startup-error.log"),
            Path.Combine(baseDirectory, "validation-sample.wav"),
            preferencesPath);
    }

    public async Task SaveModelsDirectoryAsync(string directory, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPreferences)!);
        var temporary = ModelPreferences + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(
                new RuntimeSettings { ModelsDirectory = Path.GetFullPath(directory) },
                RuntimeSettingsContext.Default.RuntimeSettings), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, ModelPreferences, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}

internal sealed record RuntimeSettings
{
    [JsonPropertyName("models_directory")]
    public string? ModelsDirectory { get; init; }
}

[JsonSerializable(typeof(RuntimeSettings))]
internal partial class RuntimeSettingsContext : JsonSerializerContext;
