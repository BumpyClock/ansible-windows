using Ansible.Core;

namespace Ansible.Tests;

public sealed class FloatingPreviewTests
{
    [Theory]
    [InlineData(DictationPhase.Preparing, "Preparing")]
    [InlineData(DictationPhase.Recording, "Listening")]
    [InlineData(DictationPhase.Finishing, "Finishing")]
    [InlineData(DictationPhase.Cancelling, "Cancelling")]
    public void LiveStatesRetainAccessibleStatusWithoutVisibleChrome(DictationPhase phase, string status)
    {
        var presentation = FloatingPreviewPresentation.From(State(phase));
        Assert.Equal(status, presentation.Status);
        Assert.False(presentation.IsTerminal);
    }

    [Fact]
    public void ReplayIsNotAMicrophoneAndHasNoManualFinish()
    {
        var presentation = FloatingPreviewPresentation.From(State(DictationPhase.Recording) with
        {
            Activity = SessionActivity.Replay
        });
        Assert.Equal("Replaying WAV", presentation.Status);
        Assert.Equal("Text appears after the WAV replay.", presentation.Hint);
    }

    [Fact]
    public void BufferedRecognitionExplainsWhenTextArrivesWithoutInventingPreview()
    {
        var recording = FloatingPreviewPresentation.From(State(DictationPhase.Recording));
        Assert.Equal("Text appears after Finish.", recording.Hint);
        var finishing = FloatingPreviewPresentation.From(State(DictationPhase.Finishing));
        Assert.Equal("Recognizing audio. Waiting for final text.", finishing.Hint);
    }

    [Fact]
    public void CaptureRemainsIndependentOfTranscriptContent()
    {
        var presentation = FloatingPreviewPresentation.From(State(DictationPhase.Recording) with
        {
            Transcript = "actual completed utterance",
            Result = new("actual completed utterance", "actual completed utterance")
        });
        Assert.False(presentation.IsTerminal);
        Assert.Equal("Text appears after Finish.", presentation.Hint);
    }

    [Fact]
    public void ErrorAndCancellationExplainWaitingWithoutTextPreview()
    {
        var cancelled = FloatingPreviewPresentation.From(State(DictationPhase.Cancelling) with { Transcript = "partial" });
        Assert.Equal("Waiting for the native step to release resources.", cancelled.Hint);
        var failed = FloatingPreviewPresentation.From(State(DictationPhase.RecoveryRequired) with
        {
            Transcript = "partial",
            Notice = new(NoticeKind.Error, "Operation failed", "Native resource release failed.")
        });
        Assert.Equal("Recognition failed", failed.Status);
        Assert.Equal("Native resource release failed.", failed.Hint);
        Assert.True(failed.IsTerminal);
    }

    [Fact]
    public void CompletedStateUsesAuthoritativeNotice()
    {
        var presentation = FloatingPreviewPresentation.From(State(DictationPhase.Ready) with
        {
            Activity = SessionActivity.None,
            Transcript = "actual result",
            Notice = new(NoticeKind.Success, "Transcript ready", "Released.")
        });
        Assert.Equal("Transcript ready", presentation.Status);
        Assert.Equal("Recognition complete. Speech stays on this device.", presentation.Hint);
        Assert.True(presentation.IsTerminal);
    }

    [Theory]
    [InlineData(NoticeKind.Error)]
    [InlineData(NoticeKind.Warning)]
    [InlineData(NoticeKind.Information)]
    public void AllOutcomesAreTerminalWithoutDelayingTheWaveformHide(NoticeKind kind)
    {
        var presentation = FloatingPreviewPresentation.From(State(DictationPhase.Ready) with
        {
            Activity = SessionActivity.None,
            Notice = new(kind, "Actual notice", "Actual explanation")
        });
        Assert.True(presentation.IsTerminal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.0625, 0.5)]
    [InlineData(0.125, 1)]
    [InlineData(1, 1)]
    [InlineData(-1, 0)]
    public void MeterUsesMeasuredEnergyOnly(double pcm, double expected) =>
        Assert.Equal(expected, FloatingPreviewPresentation.MeterLevel(pcm));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidEnergyNeverDrawsInventedLevels(double pcm) =>
        Assert.Equal(0, FloatingPreviewPresentation.MeterLevel(pcm));

    [Theory]
    [InlineData(0, 0, 1920, 1040, 1, 148, 760, 876, 400, 148)]
    [InlineData(-1920, -100, 1920, 1040, 1.5, 148, -1260, 694, 600, 222)]
    [InlineData(0, 0, 320, 200, 2, 148, 0, 0, 320, 200)]
    public void WindowFitsPhysicalWorkAreaWithoutTaskbarOverlap(
        int x, int y, int width, int height, double scale, double contentHeight,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        var bounds = FloatingPreviewPresentation.Place(x, y, width, height, scale, 400, contentHeight);
        Assert.Equal(new PreviewPlacement(expectedX, expectedY, expectedWidth, expectedHeight), bounds);
        Assert.InRange(bounds.X, x, x + width - bounds.Width);
        Assert.InRange(bounds.Y, y, y + height - bounds.Height);
    }

    [Theory]
    [InlineData(1, 850, 968, 220, 56)]
    [InlineData(1.5, 795, 932, 330, 84)]
    public void WaveformOnlyPillUsesCompactNativeBounds(
        double scale, int x, int y, int width, int height) =>
        Assert.Equal(new PreviewPlacement(x, y, width, height),
            FloatingPreviewPresentation.Place(0, 0, 1920, 1040, scale,
                FloatingPreviewPresentation.WaveformWidth, FloatingPreviewPresentation.WaveformHeight));

    [Theory]
    [InlineData(0, 4)]
    [InlineData(0.5, 16)]
    [InlineData(1, 28)]
    [InlineData(2, 28)]
    public void SilentBarsStayVisibleAndLoudBarsStayInsideThePill(double level, double expected) =>
        Assert.Equal(expected, FloatingPreviewPresentation.BarHeight(level));

    private static SessionSnapshot State(DictationPhase phase) => new()
    {
        Version = 1, Phase = phase, Activity = SessionActivity.Dictation,
        Notice = new(NoticeKind.Information, "Preparing operation", "Actual operation notice."),
        Models = [new() { Id = "moonshine-tiny", DisplayName = "Moonshine Tiny", Mode = "streaming", Preview = "final-only" }],
        SelectedIndex = 0, ModelsDirectory = "", BackendVersion = "", Transcript = ""
    };
}
