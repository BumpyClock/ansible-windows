namespace DictationPoc.Core;

public sealed record AppSettings
{
    public string? ModelId { get; init; }
    public string Language { get; init; } = "";
    public string? ModelsDirectory { get; init; }
    public DictationShortcut Shortcut { get; init; } = DictationShortcut.Default;
    public bool PushToTalk { get; init; } = true;
    public bool CollectUsage { get; init; } = true;
    public string CustomDictionary { get; init; } = "";

    public void Validate()
    {
        if (Language is null || CustomDictionary is null || ModelId is not null && string.IsNullOrWhiteSpace(ModelId) ||
            ModelsDirectory is not null && !Path.IsPathFullyQualified(ModelsDirectory) ||
            !Shortcut.IsValid)
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
