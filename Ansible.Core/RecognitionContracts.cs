using System.Threading.Channels;

namespace Ansible.Core;

/// <summary>
/// <paramref name="DisplayText"/> is the raw model text kept for diagnostics. <paramref name="SpeechText"/> is the
/// cleaned speech that insertion, word counts, and the on-screen transcript use; null when the family's output
/// cannot be read as speech, in which case the raw text is the only thing worth showing.
/// </summary>
public sealed record RecognitionResult(string DisplayText, string? SpeechText)
{
    public int? SpokenWords => SpeechText is null ? null : AudioMeter.CountWords(SpeechText);
    /// <summary>What the user sees and copies: cleaned speech, or raw text when speech is unknown.</summary>
    public string Transcript => SpeechText ?? DisplayText;
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
    Task<IAudioCapture> StartAsync(CancellationToken cancellationToken);
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
