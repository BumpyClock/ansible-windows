using System.Text.Json;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory, "settings-test-" + Guid.NewGuid().ToString("N"));

    private AppSettingsStore CreateStore() => new(Path.Combine(_directory, "settings.json"));

    [Fact]
    public void MissingSettingsUseDefaultsWithoutCreatingAFile()
    {
        var store = CreateStore();

        Assert.Equal(new AppSettings(), store.Load());
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void AllSavedPreferencesReturnAfterRestart()
    {
        var settings = new AppSettings
        {
            ModelId = "vibevoice-asr-streaming",
            Language = "en",
            ModelsDirectory = Path.Combine(_directory, "models"),
            Shortcut = new DictationShortcut(2, 0x44),
            PushToTalk = false,
            CollectUsage = false,
            CustomDictionary = "NativeAOT\nWinUI"
        };
        CreateStore().Save(settings);

        Assert.Equal(settings, CreateStore().Load());
        using var document = JsonDocument.Parse(File.ReadAllText(CreateStore().Path));
        var shortcut = document.RootElement.GetProperty("Shortcut");
        Assert.Equal(2u, shortcut.GetProperty("Modifiers").GetUInt32());
        Assert.Equal(0x44u, shortcut.GetProperty("Key").GetUInt32());
        Assert.False(shortcut.TryGetProperty("DisplayText", out _));
        Assert.False(shortcut.TryGetProperty("IsValid", out _));
    }

    [Fact]
    public void SavingDefaultsReplacesPreviousPreferences()
    {
        CreateStore().Save(new AppSettings
        {
            ModelId = "qwen3-asr",
            CustomDictionary = "WinUI",
            PushToTalk = false
        });

        CreateStore().Save(new AppSettings());

        Assert.Equal(new AppSettings(), CreateStore().Load());
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void InvalidSavePreservesThePreviousDocument()
    {
        var settings = new AppSettings { ModelId = "nemotron" };
        var store = CreateStore();
        store.Save(settings);
        var previousDocument = File.ReadAllBytes(store.Path);

        Assert.Throws<InvalidDataException>(() => store.Save(settings with
        {
            Shortcut = new DictationShortcut(0, 0x41)
        }));

        Assert.Equal(previousDocument, File.ReadAllBytes(store.Path));
        Assert.Equal(settings, CreateStore().Load());
    }

    [Theory]
    [InlineData("{", typeof(JsonException))]
    [InlineData("null", typeof(InvalidDataException))]
    [InlineData("{\"CustomDictionary\":\"WinUI\\nwinui\"}", typeof(InvalidDataException))]
    public void CorruptSettingsAreReportedWithoutReplacingTheFile(string content, Type errorType)
    {
        var store = CreateStore();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.Path, content);

        var error = Record.Exception(() => store.Load());

        Assert.NotNull(error);
        Assert.IsType(errorType, error);
        Assert.Equal(content, File.ReadAllText(store.Path));
    }

    [Fact]
    public void DictionaryNormalizationPreservesFirstSpellingAndOrder()
    {
        Assert.Equal("WinUI\nNative AOT\nCaf\u00e9",
            CustomVocabulary.Normalize("  WinUI\r\nwinui\nNative\t AOT\nCafe\u0301\n\n"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }
}
