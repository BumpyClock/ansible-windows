using System.Text.Json.Serialization;

namespace Ansible.Core;

/// <summary>How dictated text reaches the destination field.</summary>
public enum TextInsertionMethod
{
    /// <summary>Back up the clipboard, paste the text with Ctrl+V, then restore the clipboard.</summary>
    Paste,
    /// <summary>Send each character as a Unicode keystroke, separated by the typing gap.</summary>
    Type
}

public sealed record AppSettings
{
    public const int MaxTypingGapMilliseconds = 200;

    public string? ModelId { get; init; }
    public string Language { get; init; } = "";
    public NativeBackend Backend { get; init; } = NativeBackend.Cpu;
    public string? ModelsDirectory { get; init; }
    public DictationShortcut Shortcut { get; init; } = DictationShortcut.Default;
    public bool PushToTalk { get; init; } = true;
    public bool CollectUsage { get; init; } = true;
    public string CustomDictionary { get; init; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<TextInsertionMethod>))]
    public TextInsertionMethod InsertionMethod { get; init; } = TextInsertionMethod.Paste;
    // 20 ms was the shortest tested gap that typed correctly into Windows 11 Notepad; 5 ms dropped a character.
    public int TypingGapMilliseconds { get; init; } = 20;

    public void Validate()
    {
        if (Language is null || CustomDictionary is null || ModelId is not null && string.IsNullOrWhiteSpace(ModelId) ||
            ModelsDirectory is not null && !Path.IsPathFullyQualified(ModelsDirectory) ||
            !Shortcut.IsValid || Backend is not NativeBackend.Cpu and not NativeBackend.Vulkan ||
            !Enum.IsDefined(InsertionMethod) || TypingGapMilliseconds is < 0 or > MaxTypingGapMilliseconds)
            throw new InvalidDataException("The app settings contain invalid values.");
        if (CustomVocabulary.Normalize(CustomDictionary) != CustomDictionary)
            throw new InvalidDataException("The saved dictionary must contain normalized, unique entries.");
    }
}

public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
