using Ansible.Core;

namespace Ansible.Tests;

public sealed class CustomVocabularyEditTests
{
    private static readonly AudioModel Kroko = new() { Id = "kroko", Family = "kroko_asr", Mode = "streaming" };

    [Fact]
    public void AddNormalizesWhitespaceAndUnicode()
    {
        var (updated, error) = CustomVocabulary.TryApply("WinUI",
            new CustomVocabulary.Edit.Add("  Cafe\u0301\t tools  ", SingleEntry: true), null);

        Assert.Equal("WinUI\nCaf\u00e9 tools", updated);
        Assert.Null(error);
    }

    [Fact]
    public void DuplicateAddKeepsExistingSpelling()
    {
        var (updated, error) = CustomVocabulary.TryApply("WinUI",
            new CustomVocabulary.Edit.Add(" WINUI ", SingleEntry: true), null);

        Assert.Equal("WinUI", updated);
        Assert.Null(error);
    }

    [Fact]
    public void ImportSkipsDuplicatesAndKeepsPhraseBoundaries()
    {
        var (updated, error) = CustomVocabulary.TryApply("WinUI",
            new CustomVocabulary.Edit.Add(" winui\r\n Native\t AOT \nContoso"), null);

        Assert.Equal("WinUI\nNative AOT\nContoso", updated);
        Assert.Null(error);
    }

    [Fact]
    public void RenameNormalizesWithoutReorderingOtherEntries()
    {
        var (updated, error) = CustomVocabulary.TryApply("Contoso\nNative aot\nWinUI",
            new CustomVocabulary.Edit.Replace("Native aot", " Native\t AOT "), null);

        Assert.Equal("Contoso\nNative AOT\nWinUI", updated);
        Assert.Null(error);
    }

    [Fact]
    public void RenameCannotOverwriteAnotherEntry()
    {
        var (updated, error) = CustomVocabulary.TryApply("Contoso\nWinUI",
            new CustomVocabulary.Edit.Replace("Contoso", " WINUI "), null);

        Assert.Null(updated);
        Assert.Equal("That word is already in your dictionary.", error);
    }

    [Fact]
    public void RemoveKeepsOtherEntries()
    {
        var (updated, error) = CustomVocabulary.TryApply("Contoso\nWinUI",
            new CustomVocabulary.Edit.Remove("Contoso"), null);

        Assert.Equal("WinUI", updated);
        Assert.Null(error);
    }

    [Fact]
    public void MissingEntryReportsStaleRemove()
    {
        var (updated, error) = CustomVocabulary.TryApply("Contoso",
            new CustomVocabulary.Edit.Remove("WinUI"), null);

        Assert.Null(updated);
        Assert.Equal("That word is no longer in your dictionary.", error);
    }

    [Fact]
    public void MissingEntryReportsStaleRename()
    {
        var (updated, error) = CustomVocabulary.TryApply("Contoso",
            new CustomVocabulary.Edit.Replace("WinUI", "WinUI"), null);

        Assert.Null(updated);
        Assert.Equal("That word is no longer in your dictionary.", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n ")]
    [InlineData("one\ntwo")]
    [InlineData("hidden\0word")]
    public void SingleAddRejectsInvalidInput(string text)
    {
        var (updated, error) = CustomVocabulary.TryApply("Contoso",
            new CustomVocabulary.Edit.Add(text, SingleEntry: true), null);

        Assert.Null(updated);
        Assert.NotEmpty(error!);
    }

    [Fact]
    public void FullDictionaryRejectsAddWithoutReturningPartialUpdate()
    {
        var dictionary = string.Join("\n", Enumerable.Range(0, 100).Select(index => $"word{index}"));
        var (updated, error) = CustomVocabulary.TryApply(dictionary,
            new CustomVocabulary.Edit.Add("new"), null);

        Assert.Null(updated);
        Assert.Equal("The dictionary can contain at most 100 unique words or phrases.", error);
    }

    [Fact]
    public void KrokoRejectsIncompatibleAddAndRename()
    {
        CustomVocabulary.Edit[] edits = [new CustomVocabulary.Edit.Add("and/or"),
            new CustomVocabulary.Edit.Replace("Contoso", "and/or")];
        foreach (var edit in edits)
        {
            var (updated, error) = CustomVocabulary.TryApply("Contoso", edit, Kroko);

            Assert.Null(updated);
            Assert.Contains("Kroko", error);
            Assert.Contains("'/'", error);
        }
    }

    [Fact]
    public void KrokoAllowsRepairWhileOtherIncompatibleEntriesRemain()
    {
        var (updated, error) = CustomVocabulary.TryApply("and/or\nContoso",
            new CustomVocabulary.Edit.Replace("Contoso", "Northwind"), Kroko);

        Assert.Equal("and/or\nNorthwind", updated);
        Assert.Null(error);
    }

    [Fact]
    public void KrokoAllowsRemovingIncompatibleEntry()
    {
        var (updated, error) = CustomVocabulary.TryApply("and/or\nContoso",
            new CustomVocabulary.Edit.Remove("and/or"), Kroko);

        Assert.Equal("Contoso", updated);
        Assert.Null(error);
    }

    [Fact]
    public void UnchangedIncompatibleRenameCanClose()
    {
        var (updated, error) = CustomVocabulary.TryApply("and/or",
            new CustomVocabulary.Edit.Replace("and/or", "  and/or  "), Kroko);

        Assert.Equal("and/or", updated);
        Assert.Null(error);
    }

    [Fact]
    public void UndoCanRestoreSavedWordWithoutImposingCurrentModelRestrictions()
    {
        var (updated, error) = CustomVocabulary.TryApply("Contoso",
            new CustomVocabulary.Edit.Add("and/or"), null);

        Assert.Equal("Contoso\nand/or", updated);
        Assert.Null(error);
    }

    [Fact]
    public void UndoAddsToCurrentDictionaryWithoutLosingNewerWords()
    {
        var (updated, error) = CustomVocabulary.TryApply("WinUI\nNew word",
            new CustomVocabulary.Edit.Add("Contoso"), null);

        Assert.Equal("WinUI\nNew word\nContoso", updated);
        Assert.Null(error);
    }
}
