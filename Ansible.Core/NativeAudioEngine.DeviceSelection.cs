using System.Threading.Channels;

namespace Ansible.Core;

public sealed partial class NativeAudioEngine
{
    private readonly string? _deviceProbePath;
    private readonly string? _benchmarkSamplePath;
    private readonly string? _benchmarkCachePath;
    private readonly Dictionary<string, DeviceBenchmarkSelection> _deviceSelections = [];
    private readonly Dictionary<(string Id, string Mode, int History), string> _selectionKeys = [];
    private bool _streamConsumed;
    private string? _deviceCacheWarning;
    public string DeviceSelectionSummary { get; private set; } = "";
    public DeviceComparisonSnapshot? DeviceComparison { get; private set; }

    private RecognitionOptions ResolveBackend(AudioModel model, string mode, int history, long decodedBytes,
        RecognitionOptions options, CancellationToken token)
    {
        // A new selection is in progress until a complete benchmark result is available.
        DeviceComparison = null;
        try { return ResolveBackendCore(model, mode, history, decodedBytes, options, token); }
        catch (Exception error) when (options.Backend == NativeBackend.Auto)
        {
            DeviceComparison = null;
            DeviceSelectionSummary = $"Auto qualification did not select a device: {error.Message}";
            throw;
        }
    }

    private RecognitionOptions ResolveBackendCore(AudioModel model, string mode, int history, long decodedBytes,
        RecognitionOptions options, CancellationToken token)
    {
        if (options.Backend != NativeBackend.Auto)
        {
            DeviceComparison = null;
            DeviceSelectionSummary = $"Manual {options.Backend}, device {options.DeviceIndex}; no automatic benchmark or fallback.";
            return options;
        }
        token.ThrowIfCancellationRequested();
        if (_deviceProbePath is null || !File.Exists(_deviceProbePath) || _benchmarkSamplePath is null || !File.Exists(_benchmarkSamplePath))
            throw new FileNotFoundException("Auto requires the packaged Ansible.DeviceProbe.exe and public validation-sample.wav.");
        if (_inventory is null || _registry is null)
            throw new InvalidOperationException("Connect the native backend before device qualification.");
        if (!_inventory.Models.Contains(model) || model.ModelPath is null || !_inventory.Entries.TryGetValue(model.Id, out var entry))
            throw new InvalidDataException("Device qualification requires an unchanged admitted model.");
        using var lease = _integrity.OpenVerified(model.ModelPath, entry, token);
        var request = new DeviceBenchmarkRequest(_libraryPath, _catalogPath, ModelsDirectory, _benchmarkSamplePath,
            model.Id, NativeBackend.Cpu, options.DeviceIndex, _threads, _headroom, mode, history);
        var key = DeviceBenchmark.Key(request, entry.Sha256, token);
        _selectionKeys[(model.Id, mode, history)] = key;
        if (!_deviceSelections.TryGetValue(key, out var selection))
        {
            selection = DeviceBenchmark.ReadCache(_benchmarkCachePath).LastOrDefault(value => value.Key == key);
            if (selection is null)
            {
                // No resident execution weights coexist with either owned subprocess.
                ReleaseModel();
                NativeMemory.CheckBudget(entry.Bytes, decodedBytes, _headroom);
                DeviceSelectionSummary = $"Qualifying {model.Id} {mode} on CPU and Vulkan with the public sample.";
                selection = DeviceBenchmark.QualifyAsync(key, model.Family, mode,
                    backend => DeviceBenchmark.RunAsync(_deviceProbePath, request with { Backend = backend }, token), token)
                    .GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
                _deviceCacheWarning = DeviceBenchmark.TryUpdateCache(_benchmarkCachePath, key, selection);
            }
            _deviceSelections[key] = selection;
        }
        DeviceSelectionSummary = selection.Summary + (_benchmarkCachePath is null ? " Persistence disabled; this session only." : "") +
            (_deviceCacheWarning is null ? "" : " " + _deviceCacheWarning);
        DeviceComparison = ToDeviceComparison(selection);
        token.ThrowIfCancellationRequested();
        return options with { Backend = selection.Backend };
    }

    private void RejectSelectedGpu(AudioModel model, string mode, int history, NativeExecutionException error)
    {
        DeviceComparison = null;
        var key = _selectionKeys[(model.Id, mode, history)];
        var prior = _deviceSelections[key];
        if (!prior.Cpu.Working)
        {
            _deviceSelections.Remove(key);
            _deviceCacheWarning = DeviceBenchmark.TryUpdateCache(_benchmarkCachePath, key, null);
            DeviceSelectionSummary = $"Vulkan recognition failed; no qualified CPU fallback is available. {_deviceCacheWarning}";
        }
        var selection = DeviceBenchmark.CpuFallback(prior, error);
        _deviceSelections[key] = selection;
        DeviceSelectionSummary = selection.Summary + " GPU failure reported; CPU is selected for the next attempt.";
        _deviceCacheWarning = DeviceBenchmark.TryUpdateCache(_benchmarkCachePath, key, selection);
        if (_deviceCacheWarning is not null) DeviceSelectionSummary += " " + _deviceCacheWarning;
        DeviceComparison = ToDeviceComparison(selection);
    }

    private static DeviceComparisonSnapshot ToDeviceComparison(DeviceBenchmarkSelection selection) => new(
        selection.Cpu.Working ? selection.Cpu.Milliseconds : null,
        selection.Gpu.Working ? selection.Gpu.Milliseconds : null,
        selection.Backend,
        selection.MeasuredAt,
        selection.Cpu.Error,
        selection.Gpu.Error);

    private RecognitionResult RunAutoStream(AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
        IProgress<TranscriptUpdate>? progress, CancellationToken token, Action? onReady, int history = 0)
    {
        var selected = ResolveBackend(model, "streaming", history, NativeMemory.MaximumStreamFrames * sizeof(float), options, token);
        _streamConsumed = false;
        return NativeAutoRecovery.Run(options.Backend == NativeBackend.Auto, selected, token,
            backend => NativeOperation.Run(() => StreamCore(model, audio, backend, progress, token, onReady,
                history == 0 ? NativeMemory.MaximumStreamFrames : 16000L * 120 * 60, history), CleanupStream),
            error => RejectSelectedGpu(model, "streaming", history, error), () => _streamConsumed);
    }
}
