using System.Text.Json.Nodes;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class ActivationPasteCollisionTests
{
    [Theory]
    [InlineData("Shortcut", 2, PasteShortcut.ControlV)]
    [InlineData("RecordShortcut", 2, PasteShortcut.ControlV)]
    [InlineData("RecordPasteShortcut", 2, PasteShortcut.ControlV)]
    [InlineData("Shortcut", 6, PasteShortcut.ControlShiftV)]
    [InlineData("RecordShortcut", 6, PasteShortcut.ControlShiftV)]
    [InlineData("RecordPasteShortcut", 6, PasteShortcut.ControlShiftV)]
    public void EveryRegisteredActivationRejectsItsDeliveryChord(string role, uint modifiers, PasteShortcut paste)
    {
        var settings = WithShortcut(new() { RecordChordEnabled = true, PasteShortcut = paste }, role, new(modifiers, 0x56));
        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Theory]
    [InlineData("Shortcut")]
    [InlineData("RecordShortcut")]
    public void CaptureControlVIsReservedEvenWhenVoiceUsesTyping(string role)
    {
        var settings = WithShortcut(new() { RecordChordEnabled = true, InsertionMethod = TextInsertionMethod.Type }, role, new(2, 0x56));
        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Fact]
    public void DormantRecordShortcutsAndUnusedPasteChordsRemainAvailable()
    {
        new AppSettings { RecordShortcut = new(2, 0x56) }.Validate();
        new AppSettings { RecordShortcut = new(6, 0x56), PasteShortcut = PasteShortcut.ControlShiftV }.Validate();
        new AppSettings { Shortcut = new(2, 0x56), InsertionMethod = TextInsertionMethod.Type }.Validate();
        new AppSettings { RecordChordEnabled = true, RecordShortcut = new(3, 0x56) }.Validate();
    }

    [Theory]
    [InlineData("Shortcut", 2, PasteShortcut.ControlV)]
    [InlineData("RecordShortcut", 2, PasteShortcut.ControlV)]
    [InlineData("Shortcut", 6, PasteShortcut.ControlShiftV)]
    [InlineData("RecordShortcut", 6, PasteShortcut.ControlShiftV)]
    public void PreviouslyAcceptedDeliveryCollisionsLoadWithoutLosingOtherPreferences(string role, uint modifiers, PasteShortcut paste)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AnsibleActivationTests", Guid.NewGuid().ToString("N"));
        var store = new AppSettingsStore(Path.Combine(directory, "settings.json"));
        var saved = new AppSettings { RecordChordEnabled = true, Language = "fr", ModelId = "saved-model",
            IncludeVideoLink = false, CollectUsage = false, PasteShortcut = paste };
        try
        {
            store.Save(saved);
            var document = JsonNode.Parse(File.ReadAllText(store.Path))!;
            document[role]!["Modifiers"] = modifiers;
            document[role]!["Key"] = 0x56u;
            File.WriteAllText(store.Path, document.ToJsonString());
            var bytes = File.ReadAllBytes(store.Path);
            var loaded = store.Load();
            Assert.Equal(saved, loaded);
            Assert.Equal(bytes, File.ReadAllBytes(store.Path));
            store.Save(loaded with { Language = "de" });
            Assert.Equal("de", store.Load().Language);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void MigrationResetsOnlyActivationLayoutWhenReplacementDefaultWouldOverlap()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AnsibleActivationTests", Guid.NewGuid().ToString("N"));
        var store = new AppSettingsStore(Path.Combine(directory, "settings.json"));
        try
        {
            store.Save(new() { RecordChordEnabled = true, Language = "fr", ModelId = "saved-model" });
            var document = JsonNode.Parse(File.ReadAllText(store.Path))!;
            document["Shortcut"]!["Modifiers"] = 2u;
            document["Shortcut"]!["Key"] = 0x56u;
            document["RecordShortcut"]!["Modifiers"] = 10u;
            document["RecordShortcut"]!["Key"] = 0x50u;
            File.WriteAllText(store.Path, document.ToJsonString());
            var loaded = store.Load();
            Assert.Equal(new AppSettings { RecordChordEnabled = true, Language = "fr", ModelId = "saved-model" }, loaded);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnsupportedPrimaryKeyIsNotMistakenForAMigratablePasteChord()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AnsibleActivationTests", Guid.NewGuid().ToString("N"));
        var store = new AppSettingsStore(Path.Combine(directory, "settings.json"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(store.Path, """{"Shortcut":{"Modifiers":4,"Key":45},"PasteShortcut":"ShiftInsert"}""");
            Assert.Throws<InvalidDataException>(() => store.Load());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static AppSettings WithShortcut(AppSettings settings, string role, DictationShortcut shortcut) => role switch
    {
        "Shortcut" => settings with { Shortcut = shortcut },
        "RecordShortcut" => settings with { RecordShortcut = shortcut },
        "RecordPasteShortcut" => settings with { RecordPasteShortcut = shortcut },
        _ => throw new ArgumentException("Unknown activation role.", nameof(role))
    };
}
