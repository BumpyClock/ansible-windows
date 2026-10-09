using System.Diagnostics;
using System.Threading.Channels;
using Ansible.Core;
using Xunit.Abstractions;

namespace Ansible.Tests;

public sealed class NativeMeetingPipelineTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "NativeIntegration")]
    public async Task PublicSampleStreamsBeforeEndAndFinalizesWithCpuAlignment()
    {
        var workspace = FindWorkspace();
        var models = Environment.GetEnvironmentVariable("DICTATION_MODELS_DIRECTORY")
            ?? Path.Combine(workspace, ".runtime", "models");
        var library = Path.Combine(workspace, ".runtime", "native", "audiocpp.dll");
        var catalog = Path.Combine(workspace, "tools", "audio-models.json");
        var storeDirectory = Path.Combine(Path.GetTempPath(), "Ansible-native-meeting-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var pcm = await new AudioInputReader().ReadReplayAsync(
            Path.Combine(workspace, ".runtime", "validation", "sample_16k.wav"), timeout.Token);
        var capture = new ReplayCaptureFactory(pcm);
        var firstSpeech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = Stopwatch.StartNew();
        var session = new MeetingSession(
            () => new NativeAudioEngine(library, catalog, models, threads: 4), capture,
            new MeetingStore(storeDirectory), new RecognitionOptions(Backend: NativeBackend.Cpu),
            alignerFactory: () => new NativeMeetingWordAligner(library, catalog, models, threads: 4));
        session.StateChanged += state =>
        {
            if (state.Document?.Turns.Any(turn => !string.IsNullOrWhiteSpace(turn.Text)) == true)
                firstSpeech.TrySetResult();
            if (state.Phase == MeetingSessionPhase.Failed)
                firstSpeech.TrySetException(new InvalidOperationException(state.Error ?? "Native meeting pipeline failed."));
        };
        try
        {
            await session.StartAsync(new(MeetingPlaybackSource.SystemAudio)).WaitAsync(timeout.Token);
            await firstSpeech.Task.WaitAsync(timeout.Token);
            Assert.True(session.IsActive);
            Assert.False(capture.Capture.IsReleased);
            Assert.False(capture.Capture.Audio.Completion.IsCompleted);
            Assert.Equal(MeetingSessionPhase.Recording, session.State.Phase);
            Assert.Null(session.State.Error);
            output.WriteLine($"Measured: first public-sample speech through combined pipeline {timer.Elapsed.TotalSeconds:F2}s; aligned turns {session.Document!.Turns.Count(turn => turn.TimingQuality == MeetingTimingQuality.Aligned)}.");

            await session.EndAsync().WaitAsync(timeout.Token);
            Assert.Equal(MeetingSessionPhase.Completed, session.State.Phase);
            Assert.Null(session.State.Error);
            Assert.False(session.IsActive);
            Assert.True(capture.Capture.IsReleased);
            var document = await new MeetingStore(storeDirectory).LoadAsync(session.Document!.Id, timeout.Token);
            Assert.Equal(MeetingStatus.Completed, document.Status);
            Assert.NotEmpty(document.Turns);
            Assert.All(document.Turns, turn => Assert.False(turn.IsProvisional));
            Assert.Contains("Local ASR anchors", session.State.RefinementStatus ?? "");
            if (!document.Turns.Any(turn => turn.TimingQuality == MeetingTimingQuality.Aligned))
                foreach (var turn in document.Turns)
                {
                    var words = turn.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    output.WriteLine($"Public sample boundary diagnostic ({words.Length} words): {string.Join(' ', words.Take(8))} … {string.Join(' ', words.TakeLast(8))}");
                }
            Assert.Contains(document.Turns, turn => turn.TimingQuality == MeetingTimingQuality.Aligned);
            Assert.Single(Directory.GetFiles(storeDirectory));
            Assert.EndsWith(".json", Directory.GetFiles(storeDirectory)[0]);
            output.WriteLine($"Measured: combined pipeline finalized {timer.Elapsed.TotalSeconds:F2}s; final aligned turns {document.Turns.Count(turn => turn.TimingQuality == MeetingTimingQuality.Aligned)}; working set {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F1}MiB.");
        }
        finally
        {
            await session.DisposeAsync();
            if (Directory.Exists(storeDirectory))
            {
                var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                Assert.StartsWith(temporaryRoot, Path.GetFullPath(storeDirectory), StringComparison.OrdinalIgnoreCase);
                Directory.Delete(storeDirectory, recursive: true);
            }
        }
    }

    private static string FindWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "audio-models.json"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Run this test in the application workspace.");
    }

    private sealed class ReplayCaptureFactory(byte[] pcm) : IMeetingCaptureFactory
    {
        public ReplayCapture Capture { get; } = new(pcm);
        public Task<IAudioCapture> StartAsync(MeetingCaptureRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Validate();
            return Task.FromResult<IAudioCapture>(Capture);
        }
    }

    private sealed class ReplayCapture : IAudioCapture
    {
        private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(1);
        private volatile bool _released;
        public ReplayCapture(byte[] pcm)
        {
            CapturedSeconds = pcm.Length / 32000.0;
            if (!_audio.Writer.TryWrite(pcm)) throw new InvalidOperationException("Could not supply the public validation sample.");
        }
        public ChannelReader<byte[]> Audio => _audio.Reader;
        public double CapturedSeconds { get; }
        public bool IsReleased => _released;
        public event Action<double>? LevelChanged { add { } remove { } }
        public Task StopAsync() { _audio.Writer.TryComplete(); _released = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => new(StopAsync());
    }
}
