using Ansible.Core;

namespace Ansible.Tests;

public sealed class CustomVocabularyTests
{
    [Fact]
    public void AddingSeveralEntriesPreservesExistingSpellingAndPhraseBoundaries()
    {
        var result = CustomVocabulary.AddEntries("WinUI\nContoso",
            "  winui\r\nNative\t AOT\rCafe\u0301\nCONTOSO\n\n");

        Assert.Equal("WinUI\nContoso\nNative AOT\nCaf\u00e9", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n ")]
    [InlineData("WINUI\nwinui")]
    public void AddingNoNewEntriesDoesNotChangeTheDictionary(string additions) =>
        Assert.Equal("WinUI", CustomVocabulary.AddEntries("WinUI", additions));

    [Fact]
    public void PastingExistingDictionaryAtCapacityDoesNotConsumeMoreSpace()
    {
        var dictionary = string.Join("\n", Enumerable.Range(0, 40).Select(index => $"{index:00}" + new string('a', 98)))
            + "\n" + new string('b', 56);

        Assert.Equal(4096, System.Text.Encoding.UTF8.GetByteCount(dictionary));
        Assert.Equal(dictionary, CustomVocabulary.AddEntries(dictionary, dictionary));
        Assert.Throws<InvalidDataException>(() => CustomVocabulary.AddEntries(dictionary, "new"));
    }

    [Fact]
    public void AddingAnInvalidBatchRejectsTheWholeBatch()
    {
        Assert.Throws<InvalidDataException>(() =>
            CustomVocabulary.AddEntries("Contoso", "valid\n" + new string('a', 101)));
        Assert.Throws<InvalidDataException>(() =>
            CustomVocabulary.AddEntries("Contoso", "valid\nhidden\0word"));
    }

    [Fact]
    public void EntryLimitCountsOnlyNewWords()
    {
        var dictionary = string.Join("\n", Enumerable.Range(0, 100).Select(index => $"word{index}"));

        Assert.Equal(dictionary, CustomVocabulary.AddEntries(dictionary, "WORD0\nword99"));
        Assert.Throws<InvalidDataException>(() => CustomVocabulary.AddEntries(dictionary, "new word"));
    }

    [Fact]
    public void RemovingAnEntryPreservesOtherPhrasesAndOrder()
    {
        Assert.Equal("Native AOT\nWinUI", CustomVocabulary.RemoveEntry("Native AOT\nContoso\nWinUI", "Contoso"));
        Assert.Equal("", CustomVocabulary.RemoveEntry("Contoso", "Contoso"));
    }

    [Fact]
    public void RemovingAMissingEntryReportsTheStaleAction() =>
        Assert.Throws<InvalidOperationException>(() => CustomVocabulary.RemoveEntry("Contoso", "missing"));

    [Fact]
    public void EditingAnEntryNormalizesItsSpellingWithoutReorderingOtherWords() =>
        Assert.Equal("Contoso\nNative AOT\nWinUI",
            CustomVocabulary.ReplaceEntry("Contoso\nNative aot\nWinUI", "Native aot", "  Native\tAOT "));

    [Fact]
    public void EditingCanChangeOnlyTheCapitalization() =>
        Assert.Equal("WinUI", CustomVocabulary.ReplaceEntry("winui", "winui", "WinUI"));

    [Theory]
    [InlineData("")]
    [InlineData(" \n ")]
    [InlineData("first\nsecond")]
    [InlineData("hidden\0word")]
    public void EditingRejectsAnEmptyOrInvalidReplacement(string replacement) =>
        Assert.Throws<InvalidDataException>(() => CustomVocabulary.ReplaceEntry("Contoso", "Contoso", replacement));

    [Fact]
    public void EditingCannotOverwriteAnotherEntry() =>
        Assert.Throws<InvalidDataException>(() => CustomVocabulary.ReplaceEntry("Contoso\nWinUI", "Contoso", "WINUI"));

    [Fact]
    public void EditingAMissingEntryReportsTheStaleAction() =>
        Assert.Throws<InvalidOperationException>(() => CustomVocabulary.ReplaceEntry("Contoso", "missing", "replacement"));

    [Fact]
    public void EditingChecksTheWholeDictionaryByteLimit()
    {
        var dictionary = string.Join("\n", Enumerable.Range(0, 40).Select(index => $"{index:00}" + new string('a', 98)))
            + "\n" + new string('b', 56);

        Assert.Equal(dictionary, CustomVocabulary.ReplaceEntry(dictionary, new string('b', 56), new string('b', 56)));
        Assert.Throws<InvalidDataException>(() =>
            CustomVocabulary.ReplaceEntry(dictionary, new string('b', 56), new string('b', 57)));
    }
}
