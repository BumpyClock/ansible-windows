using DictationPoc.Core;

namespace DictationPoc.Tests;

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
        Assert.False(presentation.AutoDismiss);
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
        Assert.False(presentation.AutoDismiss);
    }

    [Fact]
    public void BufferedRecognitionHasNoInventedTranscript()
    {
        var recording = FloatingPreviewPresentation.From(State(DictationPhase.Recording));
        Assert.Equal("", recording.Transcript);
        Assert.Equal("Text appears after Finish.", recording.Hint);
        var finishing = FloatingPreviewPresentation.From(State(DictationPhase.Finishing));
        Assert.Equal("", finishing.Transcript);
        Assert.Equal("Recognizing audio. Waiting for final text.", finishing.Hint);
    }

    [Theory]
    [InlineData("Speaker 0: first\nsecond\nthird\nfourth")]
    [InlineData("中文😀 Καλημέρα café\r\nآخر سطر")]
    [InlineData("averylongunbrokenwordwithoutspacesatall")]
    public void PresentationPreservesFullAuthoritativeTextForAccessibility(string transcript)
    {
        var presentation = FloatingPreviewPresentation.From(State(DictationPhase.Recording) with { Transcript = transcript });
        Assert.Equal(transcript, presentation.Transcript);
        Assert.False(presentation.ShowHintWithTranscript);
    }

    [Theory]
    [InlineData("one two three four five six seven eight", "three four five six seven eight")]
    [InlineData("  one\t two\r\nthree\u00a0four five six seven  ", "two three four five six seven")]
    [InlineData("中文 😀 café Καλημέρα مرحبا بالعالم", "中文 😀 café Καλημέρα مرحبا بالعالم")]
    [InlineData("first second third fourth fifth sixth 👨‍👩‍👧‍👦", "second third fourth fifth sixth 👨‍👩‍👧‍👦")]
    [InlineData("   \r\n\t", "")]
    [InlineData("hello", "hello")]
    public void RibbonKeepsTheLastSixWholeWordsAndUnicode(string text, string expected) =>
        Assert.Equal(expected, FloatingPreviewPresentation.LastWords(text));

    [Theory]
    [InlineData(DictationPhase.Recording)]
    [InlineData(DictationPhase.Finishing)]
    public void ActualIncrementalTextUsesRibbonWithoutLosingFullTranscript(DictationPhase phase)
    {
        var transcript = "one two three four five six seven eight";
        var presentation = FloatingPreviewPresentation.From(State(phase) with
        {
            Models = [new() { Id = "live-model", Preview = "live", Mode = "streaming" }],
            Transcript = transcript
        });
        Assert.True(presentation.ShowRibbon);
        Assert.Equal("three four five six seven eight", presentation.Ribbon);
        Assert.Equal(transcript, presentation.Transcript);
    }

    [Theory]
    [InlineData("final-only")]
    [InlineData("buffered")]
    public void BufferedModelsNeverPretendToHaveIncrementalRecognition(string preview)
    {
        var presentation = FloatingPreviewPresentation.From(State(DictationPhase.Recording) with
        {
            Models = [new() { Id = "buffered-model", Preview = preview, Mode = "streaming" }]
        });
        Assert.False(presentation.ShowRibbon);
        Assert.Equal("", presentation.Ribbon);
        Assert.Equal("", presentation.Transcript);
        Assert.Equal("Text appears after Finish.", presentation.Hint);
    }

    [Theory]
    [InlineData(DictationPhase.Cancelling)]
    [InlineData(DictationPhase.Ready)]
    [InlineData(DictationPhase.RecoveryRequired)]
    public void TerminalAndCancellationStatesFreezeActualWordsAndRetainFullAccessibleText(DictationPhase phase)
    {
        var presentation = FloatingPreviewPresentation.From(State(phase) with
        {
            Models = [new() { Id = "live-model", Preview = "live", Mode = "streaming" }],
            Transcript = "one two three four five six seven eight"
        });
        Assert.True(presentation.ShowRibbon);
        Assert.Equal("three four five six seven eight", presentation.Ribbon);
        Assert.Equal("one two three four five six seven eight", presentation.Transcript);
    }

    [Fact]
    public void NewWordKeepsFiveExistingWordsInsteadOfReanimatingTheLine()
    {
        Assert.Equal([1, 2, 3, 4, 5, -1], FloatingPreviewPresentation.ReuseWords(
            ["one", "two", "three", "four", "five", "six"],
            ["two", "three", "four", "five", "six", "seven"]));
    }

    [Fact]
    public void GrowingPartialWordKeepsItsExistingAnimation()
    {
        Assert.Equal([0, 1, 2], FloatingPreviewPresentation.ReuseWords(
            ["five", "point", "bil"], ["five", "point", "billion"]));
    }

    [Fact]
    public void PartialWordKeepsItsAnimationWhenTheSixWordWindowShifts()
    {
        Assert.Equal([1, 2, 3, 4, 5, -1], FloatingPreviewPresentation.ReuseWords(
            ["over", "four", "point", "five", "bil", "year"],
            ["four", "point", "five", "bil", "years", "ago"]));
    }

    [Fact]
    public void CorrectionChangesOnlyTheCorrectedWord()
    {
        Assert.Equal([0, -1, 2], FloatingPreviewPresentation.ReuseWords(
            ["a", "nice", "sentence"], ["a", "great", "sentence"]));
    }

    [Fact]
    public void RepeatedAndRtlWordsRetainTheirLogicalOrder()
    {
        Assert.Equal([1, 2, -1], FloatingPreviewPresentation.ReuseWords(
            ["مرحبا", "مرحبا", "بالعالم"], ["مرحبا", "بالعالم", "اليوم"]));
    }

    [Fact]
    public void ErrorAndCancellationPreserveTextAndExplainWaiting()
    {
        var cancelled = FloatingPreviewPresentation.From(State(DictationPhase.Cancelling) with { Transcript = "partial" });
        Assert.True(cancelled.ShowHintWithTranscript);
        Assert.Equal("Waiting for the native step to release resources.", cancelled.Hint);
        var failed = FloatingPreviewPresentation.From(State(DictationPhase.RecoveryRequired) with
        {
            Transcript = "partial",
            Notice = new(NoticeKind.Error, "Operation failed", "Native resource release failed.")
        });
        Assert.Equal("Recognition failed", failed.Status);
        Assert.Equal("Native resource release failed.", failed.Hint);
        Assert.Equal("partial", failed.Transcript);
        Assert.True(failed.IsTerminal);
        Assert.True(failed.ShowHintWithTranscript);
        Assert.False(failed.AutoDismiss);
    }

    [Fact]
    public void CompletedStateUsesAuthoritativeNoticeAndText()
    {
        var presentation = FloatingPreviewPresentation.From(State(DictationPhase.Ready) with
        {
            Activity = SessionActivity.None,
            Transcript = "actual result",
            Notice = new(NoticeKind.Success, "Transcript ready", "Released.")
        });
        Assert.Equal("Transcript ready", presentation.Status);
        Assert.Equal("actual result", presentation.Transcript);
        Assert.Equal("Recognition complete. Speech stays on this device.", presentation.Hint);
        Assert.True(presentation.IsTerminal);
        Assert.True(presentation.AutoDismiss);
    }

    [Theory]
    [InlineData(NoticeKind.Error)]
    [InlineData(NoticeKind.Warning)]
    [InlineData(NoticeKind.Information)]
    public void OnlySuccessfulResultsAutoDismiss(NoticeKind kind)
    {
        var presentation = FloatingPreviewPresentation.From(State(DictationPhase.Ready) with
        {
            Activity = SessionActivity.None,
            Notice = new(kind, "Actual notice", "Actual explanation")
        });
        Assert.True(presentation.IsTerminal);
        Assert.False(presentation.AutoDismiss);
        Assert.Equal(kind == NoticeKind.Error, presentation.ShowHintWithTranscript);
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
    [InlineData(1, 880, 968, 160, 56)]
    [InlineData(1.5, 840, 932, 240, 84)]
    public void WaveformOnlyPillUsesCompactNativeBounds(
        double scale, int x, int y, int width, int height) =>
        Assert.Equal(new PreviewPlacement(x, y, width, height),
            FloatingPreviewPresentation.Place(0, 0, 1920, 1040, scale,
                FloatingPreviewPresentation.WaveformWidth, 56));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidEnergyNeverDrawsInventedLevels(double pcm) =>
        Assert.Equal(0, FloatingPreviewPresentation.MeterLevel(pcm));

    private static SessionSnapshot State(DictationPhase phase) => new()
    {
        Version = 1, Phase = phase, Activity = SessionActivity.Dictation,
        Notice = new(NoticeKind.Information, "Preparing operation", "Actual operation notice."),
        Models = [new() { Id = "moonshine-tiny", DisplayName = "Moonshine Tiny", Mode = "streaming", Preview = "final-only" }],
        SelectedIndex = 0, ModelsDirectory = "", BackendVersion = "", Language = "", Transcript = ""
    };
}
