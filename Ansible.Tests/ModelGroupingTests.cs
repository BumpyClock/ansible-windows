namespace Ansible;

public sealed class ModelGroupingTests
{
    private static ModelPlacement Asr(string id, bool installed = false) => new(id, IsCompanion: false, installed);
    private static ModelPlacement Companion(string id, bool installed = false) => new(id, IsCompanion: true, installed);

    private static string Ids(IReadOnlyDictionary<ModelGroup, IReadOnlyList<string>> groups, ModelGroup group) =>
        string.Join(",", groups[group]);

    [Fact]
    public void ActiveModelIsInUseEvenWithoutAFileOnDisk()
    {
        var groups = ModelGrouping.Place([Asr("a"), Asr("b", installed: true)], activeId: "a");

        Assert.Equal("a", Ids(groups, ModelGroup.InUse));
        Assert.Equal("b", Ids(groups, ModelGroup.Installed));
        Assert.Equal("", Ids(groups, ModelGroup.Available));
    }

    [Fact]
    public void InstalledCompanionIsInUseWhetherOrNotAModelIsActive()
    {
        var groups = ModelGrouping.Place([Companion("diar", installed: true), Asr("a", installed: true)], activeId: null);

        Assert.Equal("diar", Ids(groups, ModelGroup.InUse));
        Assert.Equal("a", Ids(groups, ModelGroup.Installed));
    }

    [Fact]
    public void CompanionWithoutAFileIsAvailable()
    {
        var groups = ModelGrouping.Place([Companion("diar")], activeId: "a");

        Assert.Equal("", Ids(groups, ModelGroup.InUse));
        Assert.Equal("diar", Ids(groups, ModelGroup.Available));
    }

    [Fact]
    public void InUseListsCompanionsAfterTheDictationModelAndKeepsCatalogOrderOtherwise()
    {
        var groups = ModelGrouping.Place(
            [Companion("diar-1", installed: true), Asr("a", installed: true), Companion("diar-2", installed: true), Asr("z")],
            activeId: "a");

        Assert.Equal("a,diar-1,diar-2", Ids(groups, ModelGroup.InUse));
    }

    [Fact]
    public void InstalledAndAvailableKeepCatalogOrder()
    {
        var groups = ModelGrouping.Place(
            [Asr("c"), Asr("a", installed: true), Asr("b"), Asr("d", installed: true), Asr("active", installed: true)],
            activeId: "active");

        Assert.Equal("a,d", Ids(groups, ModelGroup.Installed));
        Assert.Equal("c,b", Ids(groups, ModelGroup.Available));
    }

    [Fact]
    public void EveryGroupIsPresentForAnEmptyCatalog()
    {
        var groups = ModelGrouping.Place([], activeId: null);

        Assert.Equal(Enum.GetValues<ModelGroup>().Length, groups.Count);
        Assert.All(groups.Values, ids => Assert.Empty(ids));
    }
}
