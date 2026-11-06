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
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.WelcomeCompleted), out _))
            settings = settings with { WelcomeCompleted = true };
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.AutoDetectMeetings), out _))
            settings = settings with { AutoDetectMeetings = true };
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.Backend), out _))
            settings = settings with { Backend = NativeBackend.Auto };
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.OverlayFollowsCaret), out _))
            settings = settings with { OverlayFollowsCaret = true };
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.MeetingCleanup), out _))
            settings = settings with { MeetingCleanup = new() };
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.RecordShortcut), out _))
            settings = settings with { RecordShortcut = DictationShortcut.RecordDefault };
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.RecordPasteShortcut), out _))
            settings = settings with { RecordPasteShortcut = DictationShortcut.RecordPasteDefault };
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.IncludeVideoLink), out _))
            settings = settings with { IncludeVideoLink = true };
        // These Windows-reserved dictation chords were accepted by earlier versions.
        if (settings.Shortcut is { Modifiers: 10, Key: 0x20 } or { Modifiers: 9, Key: 0x52 })
            settings = settings with { Shortcut = DictationShortcut.Default };
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
