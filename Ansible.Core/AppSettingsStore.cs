using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansible.Core;

public sealed class AppSettingsStore(string path) : IAppSettingsStore
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public AppSettings Load()
    {
        if (!File.Exists(Path)) { return new(); }
        using var document = JsonDocument.Parse(File.ReadAllText(Path));
        var settings = JsonSerializer.Deserialize(document.RootElement, AppSettingsJsonContext.Default.AppSettings)
            ?? throw new InvalidDataException("The app settings file does not contain a settings document.");
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.AutoDetectMeetings), out _))
            settings = settings with { AutoDetectMeetings = true };
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.Backend), out _))
            settings = settings with { Backend = NativeBackend.Auto };
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.OverlayFollowsCaret), out _))
            settings = settings with { OverlayFollowsCaret = true };
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
