namespace DictationPoc.Core;

public static class DictationDeliveryPolicy
{
    public static string? SpeechForInsertion(SessionOutcome outcome) =>
        outcome.Kind == SessionOutcomeKind.Completed &&
        !string.IsNullOrWhiteSpace(outcome.Result?.SpeechText)
            ? outcome.Result.SpeechText
            : null;
}
