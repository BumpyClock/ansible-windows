using System.Threading.Channels;
using DictationPoc.Core;

namespace DictationPoc.Tests;

public sealed class NativeEngineTests
{
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
        var text = await engine.TranscribeFileAsync(model, recording, null, null, timeout.Token);
        Assert.Contains("Mother Nature", text, StringComparison.OrdinalIgnoreCase);
        var audio = Channel.CreateUnbounded<byte[]>();
        await audio.Writer.WriteAsync(PcmWave.Read16kMono(recording), timeout.Token);
        audio.Writer.Complete();
        var live = await engine.StreamAsync(model, audio.Reader, null, null, timeout.Token);
        Assert.Contains("Mother Nature", live, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(text, live);
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
