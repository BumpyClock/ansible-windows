using Ansible.Core;

namespace Ansible;

internal sealed class ModelNames
{
    private readonly Dictionary<string, HomeModelName> _catalog = new(StringComparer.OrdinalIgnoreCase);

    internal event Action? Ready;

    internal static string Plain(NativeModelEntry entry)
    {
        var name = entry.DisplayName ?? entry.Id;
        var cut = name.LastIndexOf(' ');
        if (entry.Precision is not { Length: > 0 } precision || cut <= 0) return name;
        var token = name[(cut + 1)..];
        var isPrecision = token.Equals(precision, StringComparison.OrdinalIgnoreCase)
            || token.Equals(precision.Split('_')[0], StringComparison.OrdinalIgnoreCase);
        return isPrecision ? name[..cut].TrimEnd() : name;
    }

    internal async Task LoadAsync(string catalogPath)
    {
        IReadOnlyList<NativeModelEntry> entries;
        try { entries = await Task.Run(() => NativeModelCatalog.LoadAsync(catalogPath)); }
        catch (Exception) { return; }
        foreach (var entry in entries.Where(entry => !string.IsNullOrWhiteSpace(entry.DisplayName)))
            _catalog[entry.Id] = HomeModelName.From(entry);
        Ready?.Invoke();
    }

    internal HomeModelName? Find(string modelId, IEnumerable<AudioModel> installed)
    {
        if (_catalog.TryGetValue(modelId, out var name)) return name;
        return installed.FirstOrDefault(model => model.Id == modelId)?.DisplayName is { } displayName
            ? new HomeModelName(displayName, displayName) : null;
    }
}
