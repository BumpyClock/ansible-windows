using System.Threading.Channels;
using System.Diagnostics;

namespace Ansible.Core;

public sealed partial class NativeAudioEngine
{
    private readonly string? _deviceProbePath;
    private readonly string? _benchmarkSamplePath;
    private readonly AutoDeviceQualifier _deviceQualifier;
    public string DeviceSelectionSummary { get; private set; } = "";
    public DeviceComparisonSnapshot? DeviceComparison { get; private set; }

    private T RunWithDevice<T>(AudioModel model, string mode, int history, long decodedBytes,
        RecognitionOptions options, CancellationToken token, Func<RecognitionOptions, T> execute, Func<bool>? consumed = null)
    {
        // A new selection is in progress until a complete benchmark result is available.
        DeviceComparison = null;
        var diagnostics = options.Diagnostics;
        var started = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
        if (diagnostics is not null) diagnostics.SelectionSource = null;
        QualifiedDevice? device = null;
        var selected = options;
        try
        {
            if (options.Backend == NativeBackend.Auto)
            {
                device = QualifyDevice(model, mode, history, decodedBytes, options, token);
                selected = options with { Backend = device.Backend };
            }
            else
            {
                if (diagnostics is not null) diagnostics.SelectionSource = NativeDiagnosticSource.Manual;
                DeviceSelectionSummary = $"Manual {options.Backend}, device {options.DeviceIndex}; no automatic benchmark or fallback.";
            }
            diagnostics?.Write(NativeDiagnosticStage.Selection, model, started, selected.Backend);
        }
        catch (Exception error)
        {
            diagnostics?.Write(NativeDiagnosticStage.Selection, model, started, error: error);
            if (options.Backend == NativeBackend.Auto)
            {
                DeviceComparison = null;
                DeviceSelectionSummary = $"Auto qualification did not select a device: {error.Message}";
            }
            throw;
        }
        if (device is null) return execute(selected);
        return NativeAutoRecovery.Run(true, selected, token, execute, error =>
        {
            diagnostics?.Write(NativeDiagnosticStage.Fallback, model, Stopwatch.GetTimestamp(), error: error);
            try { _deviceQualifier.RejectGpu(device, error); }
            finally { PublishDeviceQualification(diagnostics); }
        }, consumed);
    }

    private QualifiedDevice QualifyDevice(AudioModel model, string mode, int history, long decodedBytes,
        RecognitionOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_deviceProbePath is null || !File.Exists(_deviceProbePath) || _benchmarkSamplePath is null || !File.Exists(_benchmarkSamplePath))
            throw new FileNotFoundException("Auto requires the packaged Ansible.DeviceProbe.exe and public validation-sample.wav.");
        if (_inventory is null || _registry is null)
            throw new InvalidOperationException("Connect the native backend before device qualification.");
        if (!_inventory.Models.Contains(model) || model.ModelPath is null || !_inventory.Entries.TryGetValue(model.Id, out var entry))
            throw new InvalidDataException("Device qualification requires an unchanged admitted model.");
        using var lease = _integrity.OpenVerified(model.ModelPath, entry, token);
        var modelIdentity = entry.Sha256;
        var modelBytes = entry.Bytes;
        if (history > 0 && ModelSelectionPolicy.RequiresCompanion(model))
        {
            var companion = FindDiarizationModel();
            var companionEntry = _inventory.Entries[companion.Id];
            using var companionLease = _integrity.OpenVerified(companion.ModelPath!, companionEntry, token);
            modelIdentity += $"|{companionEntry.Sha256}|{NativeDiarizationSession.LatencyProfileIdentity}";
            modelBytes = checked(modelBytes + companionEntry.Bytes);
        }
        var request = new DeviceBenchmarkRequest(_libraryPath, _catalogPath, ModelsDirectory, _benchmarkSamplePath,
            model.Id, NativeBackend.Cpu, options.DeviceIndex, _threads, _headroom, mode, history,
            UseDictionary: model.Family == "kroko_asr" && options.CustomDictionary.Length > 0);
        try
        {
            return _deviceQualifier.Qualify(new(request, modelIdentity, model.Family), () =>
            {
                // No resident execution weights coexist with either owned subprocess.
                ReleaseModel();
                NativeMemory.CheckBudget(modelBytes, decodedBytes, _headroom);
            }, token);
        }
        finally { PublishDeviceQualification(options.Diagnostics); }
    }

    private void PublishDeviceQualification(NativeDiagnosticContext? diagnostics)
    {
        var status = _deviceQualifier.Current;
        DeviceSelectionSummary = status.Summary;
        DeviceComparison = status.Comparison;
        if (diagnostics is not null) diagnostics.SelectionSource = status.Source;
    }

    private StreamOutcome RunAutoStream(AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
        IProgress<TranscriptUpdate>? progress, CancellationToken token, Action? onReady, int history = 0)
    {
        _streamConsumed = false;
        return RunWithDevice(model, "streaming", history, NativeMemory.MaximumStreamFrames * sizeof(float), options, token,
            backend => NativeOperation.Run(() => StreamCore(model, audio, backend, progress, token, onReady,
                history == 0 ? NativeMemory.MaximumStreamFrames : 16000L * 120 * 60, history), CleanupStream),
            () => _streamConsumed);
    }
}
