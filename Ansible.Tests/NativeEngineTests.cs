using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class NativeEngineTests
{
    [Fact]
    public async Task RejectsUnknownBackendBeforeLoadingNativeCode()
    {
        await using var engine = new NativeAudioEngine("missing.dll", "missing.json", "models");
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            engine.TranscribeAsync(new() { Id = "test" }, new([], 16000, 1),
                new(Backend: (NativeBackend)42), null, CancellationToken.None));
        Assert.Equal("Backend", error.ParamName);
    }

    [Fact]
    public async Task RejectsNegativeDeviceBeforeLoadingNativeCode()
    {
        await using var engine = new NativeAudioEngine("missing.dll", "missing.json", "models");
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            engine.StreamAsync(new() { Id = "test" }, Channel.CreateUnbounded<byte[]>().Reader,
                new(Backend: NativeBackend.Vulkan, DeviceIndex: -1), null, CancellationToken.None));
        Assert.Equal("DeviceIndex", error.ParamName);
    }

    [Fact]
    public async Task NemotronVulkanStreamHonorsCancellationWithoutAModelSpecificRejection()
    {
        await using var engine = new NativeAudioEngine("missing.dll", "missing.json", "models");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.StreamAsync(new() { Id = "nemotron-asr-0.6b-q8", Family = "nemotron_asr" },
                Channel.CreateUnbounded<byte[]>().Reader, new(Backend: NativeBackend.Vulkan),
                null, cancellation.Token));
    }

    [Fact]
    [Trait("Category", "NativeIntegration")]
    public async Task RecognizesPublicSpeechThroughTheDllWithoutAnyService()
    {
        var root = FindWorkspace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = new NativeAudioEngine(
            Path.Combine(root, ".runtime", "native", "audiocpp.dll"),
            Path.Combine(root, "tools", "audio-models.json"),
            Path.Combine(root, ".runtime", "models"), threads: 2);
        var models = await engine.ConnectAsync(timeout.Token);
        var model = Assert.Single(models, model => model.Id == "moonshine-tiny");
        var recording = Path.Combine(root, ".runtime", "validation", "sample_16k.wav");
        var input = new AudioInputReader();
        var decoded = await input.ReadRecordingAsync(recording, timeout.Token);
        var result = await engine.TranscribeAsync(model, decoded, new(), null, timeout.Token);
        Assert.Contains("Mother Nature", result.SpeechText!, StringComparison.OrdinalIgnoreCase);
        var audio = Channel.CreateUnbounded<byte[]>();
        await audio.Writer.WriteAsync(await input.ReadReplayAsync(recording, timeout.Token), timeout.Token);
        audio.Writer.Complete();
        var live = await engine.StreamAsync(model, audio.Reader, new(), null, timeout.Token);
        Assert.Contains("Mother Nature", live.SpeechText!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(result.SpeechText, live.SpeechText);
    }

    private static string FindWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "audio-models.json")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Run native integration tests from the POC workspace after model and DLL setup.");
    }
}
