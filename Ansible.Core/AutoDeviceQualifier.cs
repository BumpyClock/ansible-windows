namespace Ansible.Core;

internal sealed record QualificationInput(DeviceBenchmarkRequest Request, string ModelFingerprint, string? Family);

internal sealed record QualifiedDevice(DeviceBenchmarkSelection Selection, string ModelId, double? SampleMilliseconds)
{
    public NativeBackend Backend => Selection.Backend;
}

internal sealed record QualificationStatus(
    string Summary = "", DeviceComparisonSnapshot? Comparison = null, NativeDiagnosticSource? Source = null);

// One engine owns this state and serializes access with its operation gate.
internal sealed class AutoDeviceQualifier(
    string? cachePath,
    Func<DeviceBenchmarkRequest, CancellationToken, Task<DeviceBenchmarkResult>> probe,
    DeviceBenchmarkIdentity? identity = null)
{
    private readonly DeviceBenchmarkIdentity _identity = identity ?? new();
    private readonly Dictionary<string, QualifiedDevice> _devices = [];
    private string? _cacheWarning;

    public QualificationStatus Current { get; private set; } = new();

    public QualifiedDevice Qualify(QualificationInput input, Action prepareProbe, CancellationToken token)
    {
        Current = new();
        try
        {
            var request = input.Request;
            var key = DeviceBenchmark.Key(request, input.ModelFingerprint, token, _identity);
            if (!_devices.TryGetValue(key, out var device))
            {
                var selection = DeviceBenchmark.ReadCache(cachePath).LastOrDefault(value => value.Key == key);
                if (selection is null)
                {
                    Current = new($"Qualifying {request.ModelId} {request.Mode} on CPU and Vulkan with the public sample.",
                        Source: NativeDiagnosticSource.Qualification);
                    // This callback releases engine residency and admits memory under the already-held gate.
                    prepareProbe();
                    token.ThrowIfCancellationRequested();
                    selection = DeviceBenchmark.QualifyAsync(key, input.Family, request.Mode,
                        backend => probe(request with { Backend = backend }, token), token).GetAwaiter().GetResult();
                    token.ThrowIfCancellationRequested();
                    _cacheWarning = DeviceBenchmark.TryUpdateCache(cachePath, key, selection);
                }
                else Current = new(Source: NativeDiagnosticSource.DiskCache);
                device = new(selection, request.ModelId,
                    request.History > 0 ? ReadSampleMilliseconds(request.Sample, token) : null);
                _devices[key] = device;
            }
            else Current = new(Source: NativeDiagnosticSource.MemoryCache);

            Current = Current with
            {
                Summary = device.Selection.Summary +
                    (cachePath is null ? " Persistence disabled; this session only." : "") +
                    (_cacheWarning is null ? "" : " " + _cacheWarning),
                Comparison = Comparison(device.Selection)
            };
            token.ThrowIfCancellationRequested();
            if (device.SampleMilliseconds is { } duration)
            {
                var factor = DeviceBenchmark.RequireMeetingRealTime(device.Selection, duration, device.ModelId);
                Current = Current with { Summary = Current.Summary + $" Meeting pace {factor:F2}x real time." };
            }
            return device;
        }
        catch (Exception error)
        {
            Current = Current with
            {
                Summary = $"Auto qualification did not select a device: {error.Message}",
                Comparison = null
            };
            throw;
        }
    }

    public QualifiedDevice RejectGpu(QualifiedDevice device, NativeExecutionException error)
    {
        var prior = device.Selection;
        Current = Current with { Comparison = null };
        if (!prior.Cpu.Working)
        {
            _devices.Remove(prior.Key);
            _cacheWarning = DeviceBenchmark.TryUpdateCache(cachePath, prior.Key, null);
            Current = Current with
            {
                Summary = $"Vulkan recognition failed; no qualified CPU fallback is available. {_cacheWarning}"
            };
        }
        var selection = DeviceBenchmark.CpuFallback(prior, error);
        var fallback = device with { Selection = selection };
        _devices[prior.Key] = fallback;
        _cacheWarning = DeviceBenchmark.TryUpdateCache(cachePath, prior.Key, selection);
        Current = Current with
        {
            Summary = selection.Summary + " GPU failure reported; CPU is selected for the next attempt." +
                (_cacheWarning is null ? "" : " " + _cacheWarning),
            Comparison = Comparison(selection)
        };
        if (fallback.SampleMilliseconds is { } duration)
            DeviceBenchmark.RequireMeetingRealTime(selection, duration, fallback.ModelId);
        return fallback;
    }

    private static double ReadSampleMilliseconds(string path, CancellationToken token)
    {
        var audio = new AudioInputReader().ReadRecordingAsync(path, token).GetAwaiter().GetResult();
        return audio.Samples.Length / (double)audio.Channels / audio.SampleRate * 1000;
    }

    private static DeviceComparisonSnapshot Comparison(DeviceBenchmarkSelection selection) => new(
        selection.Cpu.Working ? selection.Cpu.Milliseconds : null,
        selection.Gpu.Working ? selection.Gpu.Milliseconds : null,
        selection.Backend, selection.MeasuredAt, selection.Cpu.Error, selection.Gpu.Error);
}
