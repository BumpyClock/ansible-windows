using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Ansible.Core;

public sealed record MeetingAudioSourceHealth(
    long Packets, long ReceivedSamples, long SilentSamples, long MissingMixedSamples,
    double PeakInputRms, double WindowInputRms, double WindowMixedRms,
    double? LastPacketAgeSeconds, double? LastSignalAgeSeconds)
{
    public MeetingPacketTiming? LastPacketTiming { get; init; }
    [JsonIgnore]
    public string SignalStatus => LastSignalAgeSeconds is <= 5 ? "Audio detected"
        : LastPacketAgeSeconds is null ? "No audio packets"
        : LastPacketAgeSeconds > 3 ? "No recent audio packets" : "Quiet";
}

public sealed record MeetingPacketTiming(long TimestampSample, long? ExpectedSample, int Samples,
    ulong DevicePosition, ulong? PreviousDevicePosition, uint Flags);

public sealed record MeetingCaptureHealth(
    double ElapsedSeconds, int OutputDevices, int SkippedOutputDevices, [property: JsonIgnore] int? ProcessId,
    MeetingAudioSourceHealth Microphone, MeetingAudioSourceHealth Playback, [property: JsonIgnore] string? DiagnosticsError)
{
    public Guid CaptureId { get; init; }
    public long OutputDevicesLost { get; init; }
    [JsonIgnore]
    public bool PlaybackIsQuiet => ElapsedSeconds >= 15 && (Playback.LastSignalAgeSeconds is null or >= 15);
}

public interface IMeetingCaptureDiagnostics
{
    MeetingCaptureHealth? Health { get; }
    event Action<MeetingCaptureHealth>? HealthChanged;
}

public enum MeetingCaptureDiagnosticKind
{
    Starting, Recording, Sample, Pipeline, Stopped, Failed, OutputAdded, OutputSkipped, OutputLost
}
public enum MeetingCaptureStage
{
    OpenMicrophone, OpenPlayback, InitializeMicrophone, InitializePlayback,
    StartMicrophone, StartPlayback, ReadMicrophone, ReadPlayback, Mix, Stop,
    InitializeCom, MicrophoneService, PlaybackService, EnumerateOutputs
}

public enum DiagnosticFamily { MeetingCapture, Meeting, Operation, Delivery, Application, MicrophoneCapture, ModelDownload, Storage, Native, Shortcut }

public sealed record MeetingCaptureDiagnostic(
    Guid CaptureId, DateTimeOffset TimestampUtc, MeetingCaptureDiagnosticKind Kind,
    MeetingCaptureStage Stage, MeetingCaptureHealth Health, int? ErrorHResult = null,
    long DroppedEvents = 0, MeetingPipelineHealth? Pipeline = null)
{
    public DiagnosticFamily Family => DiagnosticFamily.MeetingCapture;
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public Guid? MeetingOperationId { get; init; }
    public string? AppBuild { get; init; }
}

public sealed record MeetingPipelineHealth(MeetingSessionPhase Phase, double CapturedSeconds,
    double ConsumedSeconds, long RecognitionUpdates, double? ProcessedSeconds);

public enum MeetingDiagnosticKind { Starting, Candidate, CandidateRejected, Paused, Resumed, NamingAvailability, RefinementAvailability, Terminal }
public enum MeetingDiagnosticStage { Prepare, SelectModel, Capture, Recognize, Storage, Cleanup }
public enum MeetingModelFamily { Nemotron, Kroko, VibeVoiceStreaming }
public enum MeetingDiagnosticOutcome { Completed, Failed, Cancelled, Discarded }

public sealed record MeetingDiagnostic(
    Guid OperationId, DateTimeOffset TimestampUtc, MeetingDiagnosticKind Kind, MeetingDiagnosticStage Stage,
    MeetingDiagnosticOutcome? Outcome = null, MeetingModelFamily? ModelFamily = null,
    bool? ExplicitModel = null, Guid? CaptureId = null, double? DurationMilliseconds = null,
    int? ErrorHResult = null)
{
    public DiagnosticFamily Family => DiagnosticFamily.Meeting;
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public long DroppedEvents { get; init; }
    public string? AppBuild { get; init; }
    public bool? Available { get; init; }
    public string? ModelId { get; init; }
    public bool? RetainedResources { get; init; }
    public double? RealTimeFactor { get; init; }
    public double? MaximumRealTimeFactor { get; init; }
    public NativeBackend? QualifiedBackend { get; init; }
}

public sealed record MicrophoneCaptureDiagnostic(
    Guid OperationId, DateTimeOffset TimestampUtc, long Packets, long Samples,
    double? MaximumCallbackGapMilliseconds, int BacklogHighWaterPackets,
    bool Overflowed, bool StopFailed, bool Released)
{
    public DiagnosticFamily Family => DiagnosticFamily.MicrophoneCapture;
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public long DroppedEvents { get; init; }
    public string? AppBuild { get; init; }
}

public enum DownloadDiagnosticStage { Transfer, Verify, Install }
public enum DownloadDiagnosticOutcome { Completed, Failed, Cancelled, TimedOut }
public enum DiagnosticModelFamily { Moonshine, VibeVoiceStreaming, VibeVoice, Qwen, Nemotron, Kroko, NemotronDiarization }

public sealed record DownloadDiagnostic(
    Guid AttemptId, DateTimeOffset TimestampUtc, DownloadDiagnosticStage Stage,
    DownloadDiagnosticOutcome Outcome, DiagnosticModelFamily? ModelFamily,
    double DurationMilliseconds, long? ResumeOffsetBytes = null, long? DownloadedBytes = null,
    int? HttpStatusCode = null, int? ErrorHResult = null)
{
    public DiagnosticFamily Family => DiagnosticFamily.ModelDownload;
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public long DroppedEvents { get; init; }
    public string? AppBuild { get; init; }
    public string? ModelId { get; init; }
}

public enum StorageDiagnosticCategory { Settings, Usage, History, Meeting }
public enum StorageDiagnosticStage { Load, Save, Flush, Queue }
public sealed record StorageDiagnostic(
    DateTimeOffset TimestampUtc, StorageDiagnosticCategory Category, StorageDiagnosticStage Stage,
    DiagnosticOutcome Outcome, double? DurationMilliseconds = null, int? ErrorHResult = null,
    long? DroppedWrites = null)
{
    public DiagnosticFamily Family => DiagnosticFamily.Storage;
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public long DroppedEvents { get; init; }
    public string? AppBuild { get; init; }
}

public enum NativeDiagnosticStage { Selection, Session, Fallback }
public enum NativeDiagnosticSource { Manual, MemoryCache, DiskCache, Qualification }
public sealed record NativeDiagnostic(
    Guid OperationId, DateTimeOffset TimestampUtc, NativeDiagnosticStage Stage,
    DiagnosticOutcome Outcome, NativeBackend RequestedBackend, NativeBackend? ActualBackend,
    DiagnosticModelFamily? ModelFamily, NativeDiagnosticSource? Source,
    double? DurationMilliseconds, bool? SessionReused = null, int? NativeStatusCode = null,
    int? ErrorHResult = null)
{
    public DiagnosticFamily Family => DiagnosticFamily.Native;
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public long DroppedEvents { get; init; }
    public string? AppBuild { get; init; }
    public long? RequiredMemoryBytes { get; init; }
    public ulong? AvailablePhysicalBytes { get; init; }
    public ulong? AvailableCommitBytes { get; init; }
    public bool BackgroundPreload { get; init; }
}

public enum ShortcutDiagnosticOutcome
{
    Admitted, CapturePending, DeliveryPending, SessionUnavailable, ModelIneligible,
    TargetUnavailable, Stale, Cancelled, AdmissionRejected
}
public sealed record ShortcutDiagnostic(
    Guid AttemptId, DateTimeOffset TimestampUtc, ShortcutDiagnosticOutcome Outcome,
    double DurationMilliseconds, Guid? OperationId = null, int? ErrorHResult = null)
{
    public DiagnosticFamily Family => DiagnosticFamily.Shortcut;
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public long DroppedEvents { get; init; }
    public string? AppBuild { get; init; }
}

public enum OperationDiagnosticKind { Starting, Terminal, Recovery }
public enum OperationDiagnosticStage { Admitted, ReadInput, StartStream, OpenMicrophone, StreamReady, Recognize, Cleanup }

public sealed record OperationDiagnostic(
    Guid OperationId, DateTimeOffset TimestampUtc, OperationDiagnosticKind Kind,
    SessionActivity Activity, OperationDiagnosticStage Stage,
    SessionOutcomeKind? Outcome = null, int? NativeStatusCode = null,
    bool? RetainedResources = null, double? DurationMilliseconds = null,
    double? MicrophoneReadyMilliseconds = null, double? StreamReadyMilliseconds = null,
    double? FirstPartialMilliseconds = null, double? FinishToResultMilliseconds = null)
{
    public DiagnosticFamily Family => DiagnosticFamily.Operation;
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public long DroppedEvents { get; init; }
    public int? ErrorHResult { get; init; }
    public NativeBackend? RequestedBackend { get; init; }
    public NativeBackend? ActualBackend { get; init; }
    public bool? SessionReused { get; init; }
    public string? ModelId { get; init; }
    public string? NativeBuild { get; init; }
    public double? ShortcutToMicrophoneMilliseconds { get; init; }
    public string? AppBuild { get; init; }
}

public enum ApplicationDiagnosticStage { Launch, Startup, MicrophonePermission, Hotkey, Shutdown, Recovery, CleanupCopilot, CleanupOpenAI }
public enum DiagnosticOutcome { Completed, Failed, Cancelled }

public sealed record ApplicationDiagnostic(
    DateTimeOffset TimestampUtc, ApplicationDiagnosticStage Stage, DiagnosticOutcome Outcome,
    double? DurationMilliseconds = null, int? ErrorHResult = null,
    MicrophonePermissionStatus? Permission = null)
{
    public DiagnosticFamily Family => DiagnosticFamily.Application;
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public long DroppedEvents { get; init; }
    public string? AppBuild { get; init; }
}

public enum DeliveryDiagnosticOutcome
{
    Sent, Verified, Rejected, Partial, CleanupFailed, Cancelled, Stale,
    NoAuthoritativeSpeech, EmptySpeech, RecognitionNotCompleted, Exception
}

public sealed record DeliveryDiagnostic(
    Guid OperationId, DateTimeOffset TimestampUtc, DeliveryDiagnosticOutcome Outcome,
    TextInsertionMethod Method, PasteShortcut? PasteShortcut,
    double? ResultToDeliveryMilliseconds, double DeferredMilliseconds, int? ErrorHResult = null)
{
    public DiagnosticFamily Family => DiagnosticFamily.Delivery;
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public long DroppedEvents { get; init; }
    public string? AppBuild { get; init; }
    public TextDeliveryReason? Reason { get; init; }
    public double? ResultToSendMilliseconds { get; init; }
    public double? VerificationMilliseconds { get; init; }
}

/// <summary>Bounded local metadata only. Never accepts audio, transcript text, titles, process names, or device names or IDs.</summary>
public sealed class LocalDiagnosticLog : IAsyncDisposable
{
    internal const long MaximumFileBytes = 1024 * 1024;
    private readonly string _path;
    private readonly Func<string, string, Task> _appendLine;
    private readonly object _consent = new();
    private readonly Channel<QueuedEntry> _pending = Channel.CreateBounded<QueuedEntry>(
        new BoundedChannelOptions(64) { SingleReader = true, AllowSynchronousContinuations = false });
    private Task? _writer;
    private bool _disposed;
    private readonly Guid _runId = Guid.NewGuid();
    private readonly string? _appBuild;
    private long _generation;
    private bool _enabled;
    private long _dropped;
    private string? _error;
    private sealed record QueuedEntry(long Generation, MeetingCaptureDiagnostic? Meeting = null,
        OperationDiagnostic? Operation = null, ApplicationDiagnostic? Application = null,
        DeliveryDiagnostic? Delivery = null, MeetingDiagnostic? MeetingLifecycle = null,
        MicrophoneCaptureDiagnostic? Microphone = null, DownloadDiagnostic? Download = null,
        StorageDiagnostic? Storage = null, NativeDiagnostic? Native = null, ShortcutDiagnostic? Shortcut = null);

    public LocalDiagnosticLog(string directory, Version? appBuild = null)
        : this(directory, (path, line) => File.AppendAllTextAsync(path, line), appBuild) { }

    internal LocalDiagnosticLog(string directory, Func<string, string, Task> appendLine, Version? appBuild = null)
    {
        DirectoryPath = Path.GetFullPath(directory);
        _path = Path.Combine(DirectoryPath, "local-diagnostics.jsonl");
        _appendLine = appendLine;
        _appBuild = appBuild?.ToString();
    }

    public string DirectoryPath { get; }
    public string? Error => Volatile.Read(ref _error);
    public long DroppedEvents => Interlocked.Read(ref _dropped);
    public bool Enabled { get { lock (_consent) return _enabled; } }
    public long? CurrentGeneration { get { lock (_consent) return _enabled ? _generation : null; } }

    public void SetEnabled(bool enabled)
    {
        lock (_consent)
        {
            if (_disposed) return;
            if (_enabled == enabled) return;
            _enabled = enabled;
            _generation++;
            Interlocked.Exchange(ref _dropped, 0);
            Volatile.Write(ref _error, null);
            if (enabled) _writer ??= Task.Run(WriteAsync);
        }
    }

    public void Write(MeetingCaptureDiagnostic entry)
    {
        lock (_consent)
        {
            if (!_enabled || _disposed) return;
            Queue(new(_generation, Meeting: entry));
        }
    }

    internal void Write(OperationDiagnostic entry, long generation)
    {
        lock (_consent)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            Queue(new(generation, Operation: entry));
        }
    }

    public void Write(ApplicationDiagnostic entry, long generation)
    {
        lock (_consent)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            Queue(new(generation, Application: entry));
        }
    }

    public void Write(DeliveryDiagnostic entry, long generation)
    {
        lock (_consent)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            Queue(new(generation, Delivery: entry));
        }
    }

    public void Write(MeetingDiagnostic entry, long generation)
    {
        lock (_consent)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            Queue(new(generation, MeetingLifecycle: entry));
        }
    }

    internal void Write(MicrophoneCaptureDiagnostic entry, long generation)
    {
        lock (_consent)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            Queue(new(generation, Microphone: entry));
        }
    }

    internal void Write(DownloadDiagnostic entry, long generation)
    {
        lock (_consent)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            Queue(new(generation, Download: entry));
        }
    }

    public void Write(StorageDiagnostic entry, long generation)
    {
        lock (_consent)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            Queue(new(generation, Storage: entry));
        }
    }

    internal void Write(NativeDiagnostic entry, long generation)
    {
        lock (_consent)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            Queue(new(generation, Native: entry));
        }
    }

    public void Write(ShortcutDiagnostic entry, long generation)
    {
        lock (_consent)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            Queue(new(generation, Shortcut: entry));
        }
    }

    private void Queue(QueuedEntry entry)
    {
        if (_pending.Writer.TryWrite(entry)) return;
        Interlocked.Increment(ref _dropped);
        Volatile.Write(ref _error, "Some local diagnostics could not be queued. App operation is unaffected.");
    }

    private async Task WriteAsync()
    {
        long lastWarning = 0;
        await foreach (var entry in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_consent)
            {
                if (!_enabled || entry.Generation != _generation) continue;
            }
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(_path) && new FileInfo(_path).Length >= MaximumFileBytes)
                    File.Move(_path, _path + ".previous", true);
                var dropped = Interlocked.Read(ref _dropped);
                var line = entry switch
                {
                    { Meeting: { } meeting } => JsonSerializer.Serialize(meeting with { RunId = _runId, DroppedEvents = dropped, AppBuild = _appBuild },
                        LocalDiagnosticJson.Default.MeetingCaptureDiagnostic),
                    { Operation: { } operation } => JsonSerializer.Serialize(operation with { RunId = _runId, DroppedEvents = dropped, AppBuild = _appBuild },
                        LocalDiagnosticJson.Default.OperationDiagnostic),
                    { Application: { } application } => JsonSerializer.Serialize(application with { RunId = _runId, DroppedEvents = dropped, AppBuild = _appBuild },
                        LocalDiagnosticJson.Default.ApplicationDiagnostic),
                    { Delivery: { } delivery } => JsonSerializer.Serialize(delivery with { RunId = _runId, DroppedEvents = dropped, AppBuild = _appBuild },
                        LocalDiagnosticJson.Default.DeliveryDiagnostic),
                    { MeetingLifecycle: { } meeting } => JsonSerializer.Serialize(meeting with { RunId = _runId, DroppedEvents = dropped, AppBuild = _appBuild },
                        LocalDiagnosticJson.Default.MeetingDiagnostic),
                    { Microphone: { } microphone } => JsonSerializer.Serialize(microphone with { RunId = _runId, DroppedEvents = dropped, AppBuild = _appBuild },
                        LocalDiagnosticJson.Default.MicrophoneCaptureDiagnostic),
                    { Download: { } download } => JsonSerializer.Serialize(download with { RunId = _runId, DroppedEvents = dropped, AppBuild = _appBuild },
                        LocalDiagnosticJson.Default.DownloadDiagnostic),
                    { Storage: { } storage } => JsonSerializer.Serialize(storage with { RunId = _runId, DroppedEvents = dropped, AppBuild = _appBuild },
                        LocalDiagnosticJson.Default.StorageDiagnostic),
                    { Native: { } native } => JsonSerializer.Serialize(native with { RunId = _runId, DroppedEvents = dropped, AppBuild = _appBuild },
                        LocalDiagnosticJson.Default.NativeDiagnostic),
                    { Shortcut: { } shortcut } => JsonSerializer.Serialize(shortcut with { RunId = _runId, DroppedEvents = dropped, AppBuild = _appBuild },
                        LocalDiagnosticJson.Default.ShortcutDiagnostic),
                    _ => throw new InvalidOperationException("Unknown diagnostic entry.")
                };
                await _appendLine(_path, line + Environment.NewLine).ConfigureAwait(false);
                lock (_consent)
                    if (_enabled && entry.Generation == _generation) Volatile.Write(ref _error, null);
            }
            // Any per-entry failure drops that entry. A faulted writer would make DisposeAsync throw at shutdown.
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                var current = false;
                lock (_consent)
                {
                    if (_enabled && entry.Generation == _generation)
                    {
                        current = true;
                        Interlocked.Increment(ref _dropped);
                        Volatile.Write(ref _error, "Local diagnostics could not be saved. App operation is unaffected.");
                    }
                }
                if (current && (lastWarning == 0 || Stopwatch.GetElapsedTime(lastWarning).TotalMinutes >= 1))
                {
                    Trace.TraceWarning("Ansible: local diagnostic write failed, HRESULT 0x{0:X8}.", error.HResult);
                    lastWarning = Stopwatch.GetTimestamp();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? writer;
        lock (_consent)
        {
            if (!_disposed)
            {
                _disposed = true;
                _pending.Writer.TryComplete();
            }
            writer = _writer;
        }
        if (writer is not null) await writer.ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(MeetingCaptureDiagnostic))]
[JsonSerializable(typeof(OperationDiagnostic))]
[JsonSerializable(typeof(ApplicationDiagnostic))]
[JsonSerializable(typeof(DeliveryDiagnostic))]
[JsonSerializable(typeof(MeetingDiagnostic))]
[JsonSerializable(typeof(MicrophoneCaptureDiagnostic))]
[JsonSerializable(typeof(DownloadDiagnostic))]
[JsonSerializable(typeof(StorageDiagnostic))]
[JsonSerializable(typeof(NativeDiagnostic))]
[JsonSerializable(typeof(ShortcutDiagnostic))]
internal partial class LocalDiagnosticJson : JsonSerializerContext;
