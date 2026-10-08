using System.Threading.Channels;

namespace Ansible.Core;

public sealed record RecognitionResult(string DisplayText, string? SpeechText)
{
    public int? SpokenWords => SpeechText is null ? null : AudioMeter.CountWords(SpeechText);
}

public enum NativeBackend
{
    Cpu,
    Vulkan
}

public sealed record RecognitionOptions(string? Language = null, string CustomDictionary = "",
    NativeBackend Backend = NativeBackend.Cpu, int DeviceIndex = 0)
{
    internal void ValidateBackend()
    {
        if (Backend is not NativeBackend.Cpu and not NativeBackend.Vulkan)
            throw new ArgumentOutOfRangeException(nameof(Backend));
        ArgumentOutOfRangeException.ThrowIfNegative(DeviceIndex);
    }
}

public interface IRecognitionEngine : IAsyncDisposable
{
    string ModelsDirectory { get; }
    string Version { get; }
    Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken cancellationToken);
    Task<RecognitionResult> TranscribeAsync(
        AudioModel model, WaveAudio audio, RecognitionOptions options,
        IProgress<TranscriptUpdate>? progress, CancellationToken cancellationToken);
    Task<RecognitionResult> StreamAsync(
        AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
        IProgress<TranscriptUpdate>? progress, CancellationToken cancellationToken, Action? onReady = null);
    /// <summary>Loads the model and a reusable streaming session so the next dictation starts without loading.</summary>
    Task PrepareAsync(AudioModel model, RecognitionOptions options, CancellationToken cancellationToken);
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
