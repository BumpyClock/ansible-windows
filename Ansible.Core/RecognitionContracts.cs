using System.Threading.Channels;

namespace Ansible.Core;

public sealed record RecognitionResult(string DisplayText, string? SpeechText)
{
    public int? SpokenWords => SpeechText is null ? null : AudioMeter.CountWords(SpeechText);
}

public enum NativeBackend
{
    Cpu = 0,
    Vulkan = 1,
    Auto = 2
}

/// <summary>
/// The recognition shape a device comparison measured. Offline models recognize a whole recording, which serves both
/// microphone dictation and WAV files; meetings stream with speaker history.
/// </summary>
public enum DeviceComparisonKind { Streaming, Meeting, WholeRecording }

// Each model and kind qualifies separately; ModelName and Kind say which comparison this is.
public sealed record DeviceComparisonSnapshot(
    string ModelName,
    DeviceComparisonKind Kind,
    double? CpuMilliseconds,
    double? GpuMilliseconds,
    NativeBackend RecommendedBackend,
    DateTimeOffset MeasuredAt,
    string? CpuError = null,
    string? GpuError = null);

public sealed record RecognitionOptions(string? Language = null, string CustomDictionary = "",
    NativeBackend Backend = NativeBackend.Auto, int DeviceIndex = 0)
{
    internal NativeDiagnosticContext? Diagnostics { get; init; }

    internal void ValidateBackend()
    {
        if (Backend is not NativeBackend.Cpu and not NativeBackend.Vulkan and not NativeBackend.Auto)
            throw new ArgumentOutOfRangeException(nameof(Backend));
        ArgumentOutOfRangeException.ThrowIfNegative(DeviceIndex);
    }
}

public interface IRecognitionEngine : IAsyncDisposable
{
    string ModelsDirectory { get; }
    string Version { get; }
    string DeviceSelectionSummary => "";
    DeviceComparisonSnapshot? DeviceComparison => null;
    Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken cancellationToken);
    Task<RecognitionResult> TranscribeAsync(
        AudioModel model, WaveAudio audio, RecognitionOptions options,
        IProgress<TranscriptUpdate>? progress, CancellationToken cancellationToken);
    Task<RecognitionResult> StreamAsync(
        AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
        IProgress<TranscriptUpdate>? progress, CancellationToken cancellationToken, Action? onReady = null);
    /// <summary>Loads the model and a reusable streaming session so the next dictation starts without loading.</summary>
    Task PrepareAsync(AudioModel model, RecognitionOptions options, CancellationToken cancellationToken);
    /// <summary>
    /// Measures CPU and Vulkan again for dictation with this model, ignoring cached results, and replaces the cached
    /// selection that Auto uses. Reports each device as its isolated probe starts.
    /// </summary>
    Task BenchmarkDevicesAsync(AudioModel model, RecognitionOptions options,
        IProgress<NativeBackend>? progress, CancellationToken cancellationToken);
    /// <summary>Releases the resident model and session; the next recognition loads them again.</summary>
    Task UnloadAsync(CancellationToken cancellationToken);
}

public interface IAudioCapture : IAsyncDisposable
{
    ChannelReader<byte[]> Audio { get; }
    double CapturedSeconds { get; }
    bool IsReleased { get; }
    event Action<double>? LevelChanged;
    Task StopAsync();
}

internal interface IMicrophoneCaptureDiagnostics
{
    MicrophoneCaptureDiagnostic Snapshot(Guid operationId);
}

public interface IAudioCaptureFactory
{
    Task<IAudioCapture> StartAsync(CancellationToken cancellationToken, int microphoneBoostDecibels = 0);
}

public sealed class CaptureOwnershipException(string message, IAudioCapture capture, Exception cause)
    : Exception(message, cause)
{
    public IAudioCapture Capture { get; } = capture;
}

public interface IAudioInputReader
{
    Task<WaveAudio> ReadRecordingAsync(string path, CancellationToken cancellationToken);
    Task<byte[]> ReadReplayAsync(string path, CancellationToken cancellationToken);
}

public interface IUsageStore
{
    string Path { get; }
    Task<UsageDocument> LoadAsync(CancellationToken cancellationToken = default);
    Task<UsageDocument> RecordAsync(UsageEntry entry, CancellationToken cancellationToken = default);
}
