using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansible.Core;

public sealed class AppSettingsStore(string path) : IAppSettingsStore
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public AppSettings Load()
    {
        if (!File.Exists(Path)) { return new(); }
        var settings = JsonSerializer.Deserialize(File.ReadAllText(Path), AppSettingsJsonContext.Default.AppSettings)
            ?? throw new InvalidDataException("The app settings file does not contain a settings document.");
        settings.Validate();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings));
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
