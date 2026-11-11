using System.Globalization;
using Ansible.Core;

namespace Ansible;

public sealed class ModelCardTextTests : IDisposable
{
    private const long EightyMegabytes = 80L * 1024 * 1024;
    private const long OneHundredSixtyMegabytes = 160L * 1024 * 1024;
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;

    public ModelCardTextTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose() => CultureInfo.CurrentCulture = _previousCulture;

    private static NativeModelEntry Entry(string? name = "Kroko English Community 64-L Q8", string? precision = "Q8_0",
        string family = "kroko_asr", string task = "asr", string mode = "streaming", string? preview = "live",
        long bytes = OneHundredSixtyMegabytes, bool customWords = false) => new(task)
    {
        Id = "model-id",
        Family = family,
        Mode = mode,
        Filename = "model.gguf",
        Bytes = bytes,
        Sha256 = new string('0', 64),
        Preview = preview,
        DisplayName = name,
        Precision = precision,
        SupportsCustomDictionary = customWords
    };

    private static ModelDownloadSnapshot Snapshot(ModelInstallState state, long downloaded = 0, double bytesPerSecond = 0) =>
        new(Entry(), true, state, "model.gguf", downloaded, bytesPerSecond);

    [Theory]
    [InlineData("Kroko English Community 64-L Q8", "Q8_0", "Kroko English Community 64-L")]
    [InlineData("VibeVoice ASR Streaming 1.5B Q4_K", "Q4_K", "VibeVoice ASR Streaming 1.5B")]
    [InlineData("Qwen3-ASR 0.6B Q8", null, "Qwen3-ASR 0.6B Q8")]
    [InlineData("Model Q8", "Q4_K", "Model Q8")]
    [InlineData("Q8", "Q8_0", "Q8")]
    [InlineData(null, "Q8_0", "model-id")]
    public void PlainNameDropsOnlyTheEntrysOwnPrecisionToken(string? name, string? precision, string expected) =>
        Assert.Equal(expected, ModelNames.Plain(Entry(name, precision)));

    [Theory]
    [InlineData("nemotron_3_diar", "diar", "streaming", "final-only", "Tells speakers apart in meetings")]
    [InlineData("vibevoice_asr", "asr", "offline", "final-only", "Shows text after you finish")]
    [InlineData("other_asr", "asr", "offline", "final-only", "Transcribes WAV files only")]
    [InlineData("kroko_asr", "asr", "streaming", "live", "Shows text as you speak")]
    [InlineData("qwen3_asr", "asr", "streaming", "buffered", "Shows text in chunks as you speak")]
    [InlineData("moonshine_asr", "asr", "streaming", "final-only", "Shows text after you finish")]
    [InlineData("moonshine_asr", "asr", "streaming", null, "Shows text after you finish")]
    public void BehaviorSaysWhatTheModelDoes(string family, string task, string mode, string? preview, string expected) =>
        Assert.Equal(expected, ModelCardText.Behavior(Entry(family: family, task: task, mode: mode, preview: preview)));

    [Fact]
    public void SummaryAddsDictionarySupportOnlyWhenTheModelHasIt()
    {
        Assert.Equal("Shows text as you speak", ModelCardText.Summary(Entry()));
        Assert.Equal("Shows text as you speak · Uses your Dictionary words", ModelCardText.Summary(Entry(customWords: true)));
    }

    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(60_408_448L, "58 MB")]
    [InlineData(167_756_928L, "160 MB")]
    [InlineData(930_625_888L, "888 MB")]
    [InlineData(1_048_051_712L, "1.0 GB")]
    [InlineData(1_151_272_416L, "1.1 GB")]
    [InlineData(2_117_525_248L, "2.0 GB")]
    [InlineData(9_858_644_224L, "9.2 GB")]
    public void SizeUsesWholeMegabytesAndOneDecimalOfGigabytes(long bytes, string expected) =>
        Assert.Equal(expected, ModelCardText.Size(bytes));

    [Fact]
    public void FootprintShowsSizeAndMemoryForAModelAtRest()
    {
        var memory = $"Needs about {ModelCardText.Size(Entry().EstimatedMemoryBytes)} of memory";

        Assert.Equal($"160 MB download · {memory}",
            ModelCardText.Footprint(Snapshot(ModelInstallState.NotInstalled), installed: false));
        Assert.Equal($"160 MB on disk · {memory}",
            ModelCardText.Footprint(Snapshot(ModelInstallState.Installed), installed: true));
    }

    [Fact]
    public void FootprintShowsTheTransferWhileOneRunsOrWaits()
    {
        Assert.Equal("80 MB of 160 MB · 3 MB per second", ModelCardText.Footprint(
            Snapshot(ModelInstallState.Downloading, EightyMegabytes, 3 * 1024 * 1024), installed: false));
        Assert.Equal("80 MB of 160 MB",
            ModelCardText.Footprint(Snapshot(ModelInstallState.Downloading, EightyMegabytes), installed: false));
        Assert.Equal("80 MB of 160 MB downloaded",
            ModelCardText.Footprint(Snapshot(ModelInstallState.Paused, EightyMegabytes), installed: false));
    }

    [Theory]
    [InlineData(false, ModelInstallState.Installed, "Not supported")]
    [InlineData(true, ModelInstallState.ReadyToInstall, "Ready to install")]
    [InlineData(true, ModelInstallState.Failed, "Needs attention")]
    [InlineData(true, ModelInstallState.Installed, null)]
    [InlineData(true, ModelInstallState.NotInstalled, null)]
    [InlineData(true, ModelInstallState.Downloading, null)]
    public void StateLabelNamesOnlyStatesThatNeedCallingOut(bool supported, ModelInstallState state, string? expected) =>
        Assert.Equal(expected, ModelCardText.StateLabel(supported, state));

    [Theory]
    [InlineData(ModelInstallState.NotInstalled, "Download")]
    [InlineData(ModelInstallState.Downloading, "Pause")]
    [InlineData(ModelInstallState.Verifying, "Stop")]
    [InlineData(ModelInstallState.ReadyToInstall, "Install")]
    [InlineData(ModelInstallState.Paused, "Resume")]
    [InlineData(ModelInstallState.Failed, "Retry")]
    public void PrimaryActionNamesTheNextTransferStep(ModelInstallState state, string expected) =>
        Assert.Equal(expected, ModelCardText.PrimaryAction(state));
}
