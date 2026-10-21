using Ansible.Core;

namespace Ansible.Tests;

public sealed class CustomVocabularyCompatibilityTests
{
    private static readonly AudioModel Kroko = new() { Id = "kroko", Family = "kroko_asr", Mode = "streaming" };
    private static readonly AudioModel Other = new() { Id = "other", Family = "nemotron_asr", Mode = "streaming" };

    [Fact]
    public void SlashRemainsValidGlobalVocabularyForOtherModels()
    {
        Assert.Equal("and/or", CustomVocabulary.Normalize("and/or"));
        Assert.Null(CustomVocabulary.ModelCompatibilityError(Other, "and/or"));
        Assert.Null(CustomVocabulary.ModelCompatibilityError(null, "and/or"));
    }

    [Fact]
    public void KrokoReportsActionableErrorForExistingSlashEntries()
    {
        var error = CustomVocabulary.ModelCompatibilityError(Kroko, "and/or\nContoso");

        Assert.Contains("Edit Dictionary", error);
        Assert.Contains("another model", error);
    }

    [Fact]
    public void RemovingOrRepairingOneEntryDoesNotRequireOtherEntriesToBeKrokoCompatible()
    {
        const string dictionary = "and/or\nContoso";

        Assert.Equal("Contoso", CustomVocabulary.RemoveEntry(dictionary, "and/or"));
        Assert.Equal("and/or\nNorthwind", CustomVocabulary.ReplaceEntry(dictionary, "Contoso", "Northwind"));
    }
}
