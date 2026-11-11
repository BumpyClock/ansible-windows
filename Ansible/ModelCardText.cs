using Ansible.Core;

namespace Ansible;

internal static class ModelCardText
{
    private const double Kilobyte = 1024;
    private const double Megabyte = Kilobyte * 1024;
    private const double Gigabyte = Megabyte * 1024;

    public static string Behavior(NativeModelEntry entry) =>
        ModelSelectionPolicy.IsDiarizationTask(entry.Task) ? "Tells speakers apart in meetings" :
        entry.Family == "vibevoice_asr" && entry.Mode == "offline" ? "Shows text after you finish" :
        entry.Mode == "offline" ? "Transcribes WAV files only" :
        entry.Preview == "live" ? "Shows text as you speak" :
        entry.Preview == "buffered" ? "Shows text in chunks as you speak" :
        "Shows text after you finish";

    public static string Summary(NativeModelEntry entry) =>
        entry.SupportsCustomDictionary ? $"{Behavior(entry)} · Uses your Dictionary words" : Behavior(entry);

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

    public static string? StateLabel(bool supported, ModelInstallState state) => !supported ? "Not supported" : state switch
    {
        ModelInstallState.ReadyToInstall => "Ready to install",
        ModelInstallState.Failed => "Needs attention",
        _ => null
    };

    public static string PrimaryAction(ModelInstallState state) => state switch
    {
        ModelInstallState.Downloading => "Pause",
        ModelInstallState.Verifying => "Stop",
        ModelInstallState.ReadyToInstall => "Install",
        ModelInstallState.Paused => "Resume",
        ModelInstallState.Failed => "Retry",
        _ => "Download"
    };

    public static string Size(long bytes) =>
        bytes >= 999.5 * Megabyte ? $"{bytes / Gigabyte:0.0} GB" :
        bytes >= 999.5 * Kilobyte ? $"{bytes / Megabyte:0} MB" :
        $"{bytes / Kilobyte:0} KB";
}
