using System.Text.Json.Serialization;

namespace Ansible.Core;

/// <summary>How dictated text reaches the destination field.</summary>
public enum TextInsertionMethod
{
    /// <summary>Place text on the clipboard and send the selected paste shortcut, leaving it available for delayed readers.</summary>
    Paste,
    /// <summary>Send each character as a Unicode keystroke, separated by the typing gap.</summary>
    Type
}

public enum PasteShortcut
{
    ControlV,
    ShiftInsert,
    ControlShiftV
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
    public bool HideFromCapture { get; init; }
    public bool AutoDetectMeetings { get; init; } = true;
    public string CustomDictionary { get; init; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<TextInsertionMethod>))]
    public TextInsertionMethod InsertionMethod { get; init; } = TextInsertionMethod.Paste;
    [JsonConverter(typeof(JsonStringEnumConverter<PasteShortcut>))]
    public PasteShortcut PasteShortcut { get; init; } = PasteShortcut.ControlV;
    // 20 ms was the shortest tested gap that typed correctly into Windows 11 Notepad; 5 ms dropped a character.
    public int TypingGapMilliseconds { get; init; } = 20;
    public int MicrophoneBoostDecibels { get; init; }

    public void Validate()
    {
        if (Language is null || CustomDictionary is null || ModelId is not null && string.IsNullOrWhiteSpace(ModelId) ||
            ModelsDirectory is not null && !Path.IsPathFullyQualified(ModelsDirectory) ||
            !Shortcut.IsValid || Backend is not NativeBackend.Cpu and not NativeBackend.Vulkan ||
            !Enum.IsDefined(InsertionMethod) || !Enum.IsDefined(PasteShortcut) ||
            TypingGapMilliseconds is < 0 or > MaxTypingGapMilliseconds ||
            MicrophoneBoostDecibels is < 0 or > MicrophoneBoost.MaxDecibels)
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
