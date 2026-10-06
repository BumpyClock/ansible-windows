using DictationPoc.Core;

namespace DictationPoc.Tests;

public sealed class DictationDeliveryPolicyTests
{
    [Fact]
    public void UsesAuthoritativeSpeechInsteadOfPresentationLabels()
    {
        var outcome = new SessionOutcome(SessionOutcomeKind.Completed,
            new RecognitionResult("Speaker 0: hello world", "hello world"));

        Assert.Equal("hello world", DictationDeliveryPolicy.SpeechForInsertion(outcome));
    }

    [Theory]
    [InlineData(SessionOutcomeKind.Cancelled)]
    [InlineData(SessionOutcomeKind.TimedOut)]
    [InlineData(SessionOutcomeKind.Failed)]
    public void NeverInsertsForUnsuccessfulOperations(SessionOutcomeKind kind)
    {
        var outcome = new SessionOutcome(kind, new RecognitionResult("partial text", "partial text"));

        Assert.Null(DictationDeliveryPolicy.SpeechForInsertion(outcome));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void UnknownOrBlankSpeechIsNotReplacedByDisplayText(string? speech)
    {
        var outcome = new SessionOutcome(SessionOutcomeKind.Completed,
            new RecognitionResult("Speaker 0: hello", speech));

        Assert.Null(DictationDeliveryPolicy.SpeechForInsertion(outcome));
    }
}
