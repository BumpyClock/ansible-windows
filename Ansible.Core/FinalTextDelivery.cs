namespace Ansible.Core;

public enum TextDeliveryStatus
{
    Sent,
    // Nothing was inserted. Retry after physical modifiers are released.
    Deferred,
    // Focus/target loss or a partial send. Never retry a potentially partial insertion.
    Rejected
}

public readonly record struct TextDeliveryOutcome(TextDeliveryStatus Status, string Message)
{
    public static TextDeliveryOutcome Sent(string message) => new(TextDeliveryStatus.Sent, message);
    public static TextDeliveryOutcome Deferred(string message) => new(TextDeliveryStatus.Deferred, message);
    public static TextDeliveryOutcome Rejected(string message) => new(TextDeliveryStatus.Rejected, message);
}

/// <summary>Inserts authoritative speech once, only after successful recognition completes.</summary>
public sealed class FinalTextDelivery(
    Func<string, CancellationToken, Task<TextDeliveryOutcome>> insert,
    Func<bool> isCurrent,
    Func<bool> typingDeferred)
{
    public bool Sent { get; private set; }
    public string? Error { get; private set; }

    public async Task<SessionOutcome> RunAsync(Task<SessionOutcome> operation, CancellationToken cancellationToken)
    {
        var outcome = await operation.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (outcome.Kind != SessionOutcomeKind.Completed || !isCurrent()) { return outcome; }
        var speech = outcome.Result?.SpeechText;
        if (speech is null)
        {
            Error = "The model did not return authoritative speech text.";
            return outcome;
        }
        if (string.IsNullOrWhiteSpace(speech)) { return outcome; }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isCurrent()) { return outcome; }
            if (!typingDeferred())
            {
                TextDeliveryOutcome delivery;
                try { delivery = await insert(speech, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) { Error = error.Message; return outcome; }
                if (delivery.Status == TextDeliveryStatus.Sent) { Sent = true; return outcome; }
                if (delivery.Status != TextDeliveryStatus.Deferred) { Error = delivery.Message; return outcome; }
            }
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }
    }
}
