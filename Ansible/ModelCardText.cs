using Ansible.Core;

namespace Ansible;

/// <summary>
/// The words a model row shows, in plain language first. Everything derives from the pinned catalog entry and the
/// download snapshot; exact identifiers stay in the row's details. Pure, so the wording is tested without WinUI.
/// </summary>
internal static class ModelCardText
{
    private const double Kilobyte = 1024;
    private const double Megabyte = Kilobyte * 1024;
    private const double Gigabyte = Megabyte * 1024;

    /// <summary>
    /// The catalog display name without a trailing quantization token such as "Q8" or "Q4_K". The token is removed
    /// only when it is the entry's precision, or the part of it before the underscore, so other names stay whole.
    /// </summary>
    public static string PlainName(NativeModelEntry entry)
    {
        var name = entry.DisplayName ?? entry.Id;
        var cut = name.LastIndexOf(' ');
        if (entry.Precision is not { Length: > 0 } precision || cut <= 0) return name;
        var token = name[(cut + 1)..];
        var isPrecision = token.Equals(precision, StringComparison.OrdinalIgnoreCase)
            || token.Equals(precision.Split('_')[0], StringComparison.OrdinalIgnoreCase);
        return isPrecision ? name[..cut].TrimEnd() : name;
    }

    /// <summary>What the model does for the person dictating, from its task, mode, and preview capability.</summary>
    public static string Behavior(NativeModelEntry entry) =>
        ModelSelectionPolicy.IsDiarizationTask(entry.Task) ? "Tells speakers apart in meetings" :
        entry.Family == "vibevoice_asr" && entry.Mode == "offline" ? "Shows text after you finish" :
        entry.Mode == "offline" ? "Transcribes WAV files only" :
        entry.Preview == "live" ? "Shows text as you speak" :
        entry.Preview == "buffered" ? "Shows text in chunks as you speak" :
        "Shows text after you finish";

    /// <summary>The first line under the name: what the model does, then whether it uses the Dictionary.</summary>
    public static string Summary(NativeModelEntry entry) =>
        entry.SupportsCustomDictionary ? $"{Behavior(entry)} · Uses your Dictionary words" : Behavior(entry);

    /// <summary>
    /// The second line under the name: the transfer while one runs or waits, otherwise the size and the memory the
    /// model needs. Memory is the admission estimate made before loading, not a measurement.
    /// </summary>
    public static string Footprint(ModelDownloadSnapshot snapshot, bool installed)
    {
        var entry = snapshot.Model;
        var transfer = $"{Size(snapshot.DownloadedBytes)} of {Size(entry.Bytes)}";
        var memory = $"Needs about {Size(entry.EstimatedMemoryBytes)} of memory";
        return snapshot.State switch
        {
            ModelInstallState.Downloading => snapshot.BytesPerSecond > 0
                ? $"{transfer} · {Size((long)snapshot.BytesPerSecond)} per second" : transfer,
            ModelInstallState.Paused => $"{transfer} downloaded",
            _ when installed => $"{Size(entry.Bytes)} on disk · {memory}",
            _ => $"{Size(entry.Bytes)} download · {memory}"
        };
    }

    /// <summary>A state the row must call out. Installed and available are the page's groups, so they have no label.</summary>
    public static string? StateLabel(bool supported, ModelInstallState state) => !supported ? "Not supported" : state switch
    {
        ModelInstallState.ReadyToInstall => "Ready to install",
        ModelInstallState.Failed => "Needs attention",
        _ => null
    };

    /// <summary>The row's one transfer or install action for a model that is not installed.</summary>
    public static string PrimaryAction(ModelInstallState state) => state switch
    {
        ModelInstallState.Downloading => "Pause",
        ModelInstallState.Verifying => "Stop",
        ModelInstallState.ReadyToInstall => "Install",
        ModelInstallState.Paused => "Resume",
        ModelInstallState.Failed => "Retry",
        _ => "Download"
    };

    /// <summary>
    /// A file size as Windows shows it: binary multiples labelled KB, MB, and GB. Gigabytes keep one decimal; smaller
    /// sizes are whole numbers. A value that would round up to 1,000 of a unit moves to the next unit.
    /// </summary>
    public static string Size(long bytes) =>
        bytes >= 999.5 * Megabyte ? $"{bytes / Gigabyte:0.0} GB" :
        bytes >= 999.5 * Kilobyte ? $"{bytes / Megabyte:0} MB" :
        $"{bytes / Kilobyte:0} KB";
}
