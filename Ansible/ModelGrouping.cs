namespace Ansible;

/// <summary>The page section a model row sits in.</summary>
internal enum ModelGroup { InUse, Installed, Available }

/// <summary>What the grouping rules need to know about one catalog row.</summary>
internal sealed record ModelPlacement(string Id, bool IsCompanion, bool HasModelFile);

internal static class ModelGrouping
{
    /// <summary>
    /// In use holds the dictation model, then an installed diarization companion. Installed holds other model files.
    /// Available holds the rest, including transfers, failures, and a companion that is not installed yet.
    /// </summary>
    public static ModelGroup GroupOf(ModelPlacement model, string? activeId) =>
        model.Id == activeId || model.IsCompanion && model.HasModelFile ? ModelGroup.InUse
        : model.HasModelFile ? ModelGroup.Installed
        : ModelGroup.Available;

    /// <summary>
    /// Row ids per group. Rows keep catalog order inside a group, except that In use lists companions after the
    /// dictation model. Every group is present, empty or not.
    /// </summary>
    public static IReadOnlyDictionary<ModelGroup, IReadOnlyList<string>> Place(IEnumerable<ModelPlacement> models, string? activeId)
    {
        var rows = models.ToList();
        return Enum.GetValues<ModelGroup>().ToDictionary(group => group, group => (IReadOnlyList<string>)
            [.. rows.Where(model => GroupOf(model, activeId) == group).OrderBy(model => group == ModelGroup.InUse && model.IsCompanion)
                .Select(model => model.Id)]);
    }
}
