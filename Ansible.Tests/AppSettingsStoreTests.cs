using System.Text.Json;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory, "settings-test-" + Guid.NewGuid().ToString("N"));

    private AppSettingsStore CreateStore() => new(Path.Combine(_directory, "settings.json"));

    [Fact]
    public void FreshInstallShowsWelcomeAndCompletionSurvivesRestart()
    {
        var store = CreateStore();
        Assert.False(store.Load().WelcomeCompleted);
        store.Save(new AppSettings { WelcomeCompleted = true, CollectUsage = false });
        Assert.True(CreateStore().Load().WelcomeCompleted);
        Assert.False(CreateStore().Load().CollectUsage);
    }

    [Fact]
    public void ExistingInstallKeepsItsShortcutAndDoesNotForceWelcome()
    {
        var store = CreateStore();
        store.Save(new AppSettings { Shortcut = new(0, 0x78), CollectUsage = false });
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!.AsObject();
        Assert.True(document.Remove("WelcomeCompleted"));
        File.WriteAllText(store.Path, document.ToJsonString());

        var settings = store.Load();
        Assert.True(settings.WelcomeCompleted);
        Assert.Equal(new DictationShortcut(0, 0x78), settings.Shortcut);
        Assert.False(settings.CollectUsage);
    }

    [Fact]
    public void CleanupPreferencesRoundTripWithoutCredentialsOrDerivedOptions()
    {
        var cleanup = new MeetingCleanupSettings { Automatic = true, Provider = MeetingCleanupProvider.OpenAI,
            CopilotModel = "copilot-model", CopilotReasoningEffort = "low",
            OpenAIModel = "api-model", OpenAIReasoningEffort = "medium" };
        var store = CreateStore();
        store.Save(new() { MeetingCleanup = cleanup });
        Assert.Equal(cleanup, store.Load().MeetingCleanup);
        var json = File.ReadAllText(store.Path);
        Assert.DoesNotContain("ApiKey", json);
        Assert.DoesNotContain("Options", json);
        Assert.Contains("\"OpenAI\"", json);
    }

    [Fact]
    public void OlderSettingsDoNotOptIntoAutomaticUploads()
    {
        var store = CreateStore();
        store.Save(new());
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!.AsObject();
        Assert.True(document.Remove("MeetingCleanup"));
        File.WriteAllText(store.Path, document.ToJsonString());
        Assert.False(store.Load().MeetingCleanup.Automatic);
        Assert.Equal("gpt-6-luna", store.Load().MeetingCleanup.CopilotModel);
    }

    [Fact]
    public void AutomaticOpenAIRequiresModelAndInvalidProvidersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CreateStore().Save(new() { MeetingCleanup = new()
            { Automatic = true, Provider = MeetingCleanupProvider.OpenAI } }));
        Assert.Throws<InvalidDataException>(() => CreateStore().Save(new() { MeetingCleanup = new()
            { Provider = (MeetingCleanupProvider)99 } }));
    }

    [Fact]
    public void MissingSettingsUseDefaultsWithoutCreatingAFile()
    {
        var store = CreateStore();

        Assert.Equal(new AppSettings(), store.Load());
        Assert.Equal(NativeBackend.Auto, store.Load().Backend);
        Assert.Equal(0, store.Load().MicrophoneBoostDecibels);
        Assert.False(store.Load().HideFromCapture);
        Assert.False(store.Load().LocalDiagnostics);
        Assert.True(store.Load().CollectUsage);
        Assert.True(store.Load().AutoDetectMeetings);
        Assert.True(store.Load().OverlayFollowsCaret);
        Assert.Equal(PasteShortcut.ControlV, store.Load().PasteShortcut);
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void SavedSettingsWithoutBoostUseZeroDecibels()
    {
        var store = CreateStore();
        store.Save(new AppSettings { Language = "en" });
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!.AsObject();
        Assert.True(document.Remove("MicrophoneBoostDecibels"));
        File.WriteAllText(store.Path, document.ToJsonString());

        Assert.Equal(0, store.Load().MicrophoneBoostDecibels);
        Assert.Equal("en", store.Load().Language);
    }

    [Theory]
    [InlineData(NativeBackend.Auto)]
    [InlineData(NativeBackend.Cpu)]
    [InlineData(NativeBackend.Vulkan)]
    public void RecognitionProcessorRoundTrips(NativeBackend backend)
    {
        CreateStore().Save(new AppSettings { Backend = backend });
        Assert.Equal(backend, CreateStore().Load().Backend);
    }

    [Fact]
    public void SavedSettingsWithoutProcessorUseAuto()
    {
        var store = CreateStore();
        store.Save(new AppSettings { Backend = NativeBackend.Cpu });
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!.AsObject();
        Assert.True(document.Remove("Backend"));
        File.WriteAllText(store.Path, document.ToJsonString());

        Assert.Equal(NativeBackend.Auto, store.Load().Backend);
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
            LocalDiagnostics = true,
            CustomDictionary = "NativeAOT\nWinUI",
            InsertionMethod = TextInsertionMethod.Type,
            PasteShortcut = PasteShortcut.ShiftInsert,
            TypingGapMilliseconds = 35,
            MicrophoneBoostDecibels = 20,
            HideFromCapture = true,
            AutoDetectMeetings = false,
            OverlayFollowsCaret = false
        };
        CreateStore().Save(settings);

        Assert.Equal(settings, CreateStore().Load());
        using var document = JsonDocument.Parse(File.ReadAllText(CreateStore().Path));
        var shortcut = document.RootElement.GetProperty("Shortcut");
        Assert.Equal(2u, shortcut.GetProperty("Modifiers").GetUInt32());
        Assert.Equal(0x44u, shortcut.GetProperty("Key").GetUInt32());
        Assert.False(shortcut.TryGetProperty("DisplayText", out _));
        Assert.False(shortcut.TryGetProperty("IsValid", out _));
        Assert.Equal("Type", document.RootElement.GetProperty("InsertionMethod").GetString());
        Assert.Equal("ShiftInsert", document.RootElement.GetProperty("PasteShortcut").GetString());
        Assert.Equal(35, document.RootElement.GetProperty("TypingGapMilliseconds").GetInt32());
        Assert.True(document.RootElement.GetProperty("HideFromCapture").GetBoolean());
        Assert.False(document.RootElement.GetProperty("AutoDetectMeetings").GetBoolean());
        Assert.False(document.RootElement.GetProperty("OverlayFollowsCaret").GetBoolean());
    }

    [Fact]
    public void MissingDiagnosticPreferenceRemainsOffIndependentlyOfUsage()
    {
        var store = CreateStore();
        store.Save(new AppSettings { LocalDiagnostics = true, CollectUsage = true });
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!.AsObject();
        Assert.True(document.Remove(nameof(AppSettings.LocalDiagnostics)));
        File.WriteAllText(store.Path, document.ToJsonString());
        Assert.False(store.Load().LocalDiagnostics);
        Assert.True(store.Load().CollectUsage);
    }

    [Fact]
    public void ExistingSettingsWithoutOverlayPositionFollowTheCaret()
    {
        var store = CreateStore();
        store.Save(new AppSettings { Language = "en", OverlayFollowsCaret = false });
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!.AsObject();
        Assert.True(document.Remove("OverlayFollowsCaret"));
        File.WriteAllText(store.Path, document.ToJsonString());
        Assert.True(store.Load().OverlayFollowsCaret);
        Assert.Equal("en", store.Load().Language);
    }

    [Fact]
    public void ExistingSettingsWithoutCapturePrivacyKeepWindowsVisible()
    {
        var store = CreateStore();
        store.Save(new AppSettings { Language = "en" });
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!.AsObject();
        Assert.True(document.Remove("HideFromCapture"));
        File.WriteAllText(store.Path, document.ToJsonString());
        Assert.False(store.Load().HideFromCapture);
        Assert.Equal("en", store.Load().Language);
    }

    [Fact]
    public void ExistingSettingsWithoutMeetingDetectionEnableSuggestions()
    {
        var store = CreateStore();
        store.Save(new AppSettings { Language = "en", AutoDetectMeetings = false });
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!.AsObject();
        Assert.True(document.Remove("AutoDetectMeetings"));
        File.WriteAllText(store.Path, document.ToJsonString());
        Assert.True(store.Load().AutoDetectMeetings);
        Assert.Equal("en", store.Load().Language);
    }

    [Fact]
    public void ExistingSettingsWithoutPasteShortcutUseControlV()
    {
        var store = CreateStore();
        store.Save(new AppSettings { PasteShortcut = PasteShortcut.ControlShiftV });
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!.AsObject();
        Assert.True(document.Remove("PasteShortcut"));
        File.WriteAllText(store.Path, document.ToJsonString());

        Assert.Equal(PasteShortcut.ControlV, store.Load().PasteShortcut);
    }

    [Theory]
    [InlineData(PasteShortcut.ControlV)]
    [InlineData(PasteShortcut.ShiftInsert)]
    [InlineData(PasteShortcut.ControlShiftV)]
    public void PasteShortcutRoundTrips(PasteShortcut shortcut)
    {
        CreateStore().Save(new AppSettings { PasteShortcut = shortcut });
        Assert.Equal(shortcut, CreateStore().Load().PasteShortcut);
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
    [InlineData("{\"Language\":null}", typeof(InvalidDataException))]
    [InlineData("{\"CustomDictionary\":null}", typeof(InvalidDataException))]
    [InlineData("{\"ModelId\":\"\"}", typeof(InvalidDataException))]
    [InlineData("{\"ModelId\":\"   \"}", typeof(InvalidDataException))]
    [InlineData("{\"ModelsDirectory\":\"relative-models\"}", typeof(InvalidDataException))]
    [InlineData("{\"InsertionMethod\":\"Dictate\"}", typeof(JsonException))]
    [InlineData("{\"InsertionMethod\":7}", typeof(InvalidDataException))]
    [InlineData("{\"PasteShortcut\":\"Unknown\"}", typeof(JsonException))]
    [InlineData("{\"PasteShortcut\":7}", typeof(InvalidDataException))]
    [InlineData("{\"TypingGapMilliseconds\":-1}", typeof(InvalidDataException))]
    [InlineData("{\"TypingGapMilliseconds\":201}", typeof(InvalidDataException))]
    [InlineData("{\"MicrophoneBoostDecibels\":-1}", typeof(InvalidDataException))]
    [InlineData("{\"MicrophoneBoostDecibels\":25}", typeof(InvalidDataException))]
    public void CorruptSettingsAreReportedWithoutReplacingTheFile(string content, Type errorType)
    {
        var store = CreateStore();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.Path, content);
        var previousDocument = File.ReadAllBytes(store.Path);

        var error = Record.Exception(() => store.Load());

        Assert.NotNull(error);
        Assert.IsAssignableFrom(errorType, error);
        Assert.Equal(previousDocument, File.ReadAllBytes(store.Path));
    }

    [Theory]
    [InlineData("Language")]
    [InlineData("CustomDictionary")]
    [InlineData("EmptyModelId")]
    [InlineData("WhitespaceModelId")]
    [InlineData("ModelsDirectory")]
    public void InvalidScalarSavePreservesPreviousDocument(string invalidField)
    {
        var settings = new AppSettings { ModelId = "nemotron" };
        var store = CreateStore();
        store.Save(settings);
        var previousDocument = File.ReadAllBytes(store.Path);
        var invalidSettings = invalidField switch
        {
            "Language" => settings with { Language = null! },
            "CustomDictionary" => settings with { CustomDictionary = null! },
            "EmptyModelId" => settings with { ModelId = "" },
            "WhitespaceModelId" => settings with { ModelId = "   " },
            "ModelsDirectory" => settings with { ModelsDirectory = "relative-models" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField))
        };

        Assert.Throws<InvalidDataException>(() => store.Save(invalidSettings));

        Assert.Equal(previousDocument, File.ReadAllBytes(store.Path));
        Assert.Equal(settings, store.Load());
    }

    [Fact]
    public void DictionaryAcceptsOneHundredEntriesButRejectsOneHundredAndOne()
    {
        var dictionary = string.Join("\n", Enumerable.Range(0, 100).Select(index => $"word{index}"));

        Assert.Equal(dictionary, CustomVocabulary.Normalize(dictionary));
        Assert.Throws<InvalidDataException>(() => CustomVocabulary.Normalize(dictionary + "\nextra"));
    }

    [Fact]
    public void DictionaryAcceptsOneHundredEntryCharactersButRejectsOneHundredAndOne()
    {
        var entry = new string('a', 100);

        Assert.Equal(entry, CustomVocabulary.Normalize(entry));
        Assert.Throws<InvalidDataException>(() => CustomVocabulary.Normalize(entry + "a"));
    }

    [Theory]
    [InlineData("\U0001F600", "\U0001F600")]
    [InlineData("e\u0301", "\u00e9")]
    [InlineData("q\u0301", "q\u0301")]
    public void DictionaryCountsOneHundredUnicodeTextElementsAfterNormalization(
        string inputElement, string normalizedElement)
    {
        var input = string.Concat(Enumerable.Repeat(inputElement, 100));
        var expected = string.Concat(Enumerable.Repeat(normalizedElement, 100));

        Assert.Equal(expected, CustomVocabulary.Normalize(input));
        Assert.Throws<InvalidDataException>(() => CustomVocabulary.Normalize(input + inputElement));
    }

    [Fact]
    public void DictionaryAcceptsFourThousandNinetySixUtf8BytesButRejectsTheNextByte()
    {
        var entries = Enumerable.Range(0, 20)
            .Select(index => index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + new string('\u00e9', 98));
        var dictionary = string.Join("\n", entries) + "\n" + new string('\u00e9', 58);

        Assert.Equal(4096, System.Text.Encoding.UTF8.GetByteCount(dictionary));
        Assert.Equal(4097, System.Text.Encoding.UTF8.GetByteCount(dictionary + "a"));
        Assert.Equal(dictionary, CustomVocabulary.Normalize(dictionary));
        Assert.Throws<InvalidDataException>(() => CustomVocabulary.Normalize(dictionary + "a"));
    }

    [Fact]
    public void DictionaryRejectsEmbeddedControlCharacters() =>
        Assert.Throws<InvalidDataException>(() => CustomVocabulary.Normalize("word\0phrase"));

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
