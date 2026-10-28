using System.Diagnostics;

namespace Ansible.Core;

public enum TextDeliveryStatus
{
    Sent,
    Verified,
    // Nothing was inserted. Retry after physical modifiers are released.
    Deferred,
    // Insertion was stopped. Only Deferred permits automatic retry.
    Rejected,
    Partial,
    CleanupFailed
}

public enum TextDeliveryReason
{
    NoSpeech, Superseded, ModifiersHeld, TargetInvalid, ClipboardChanged, ClipboardBusy,
    ClipboardPreparationFailed, ClipboardPublishFailed, ClipboardCloseFailed, InputNotQueued,
    InputPartial, KeyReleaseFailed, CancelledAfterInput, TargetUnavailableAfterInput,
    SentUnverified, Verified
}

public readonly record struct TextDeliveryOutcome(TextDeliveryStatus Status, string Message)
{
    public TextDeliveryReason? Reason { get; init; }
    public double? VerificationMilliseconds { get; init; }
    public static TextDeliveryOutcome Sent(string message, TextDeliveryReason? reason = null) => new(TextDeliveryStatus.Sent, message) { Reason = reason };
    public static TextDeliveryOutcome Verified(string message, TextDeliveryReason? reason = null) => new(TextDeliveryStatus.Verified, message) { Reason = reason };
    public static TextDeliveryOutcome Deferred(string message, TextDeliveryReason? reason = null) => new(TextDeliveryStatus.Deferred, message) { Reason = reason };
    public static TextDeliveryOutcome Rejected(string message, TextDeliveryReason? reason = null) => new(TextDeliveryStatus.Rejected, message) { Reason = reason };
    public static TextDeliveryOutcome Partial(string message, TextDeliveryReason? reason = null) => new(TextDeliveryStatus.Partial, message) { Reason = reason };
    public static TextDeliveryOutcome CleanupFailed(string message, TextDeliveryReason? reason = null) => new(TextDeliveryStatus.CleanupFailed, message) { Reason = reason };
}

public readonly record struct DeliveryTrace(DeliveryDiagnosticOutcome Outcome,
    double? ResultToDeliveryMilliseconds, double DeferredMilliseconds, int? ErrorHResult,
    TextDeliveryReason? Reason, double? ResultToSendMilliseconds, double? VerificationMilliseconds);

/// <summary>Inserts authoritative speech once, only after successful recognition completes.</summary>
public sealed class FinalTextDelivery(
    Func<string, CancellationToken, Task<TextDeliveryOutcome>> insert,
    Func<bool> isCurrent,
    Func<bool> typingDeferred,
    Action<DeliveryTrace>? diagnostic = null)
{
    public bool Sent { get; private set; }
    public string? Error { get; private set; }
    public TextDeliveryOutcome? Delivery { get; private set; }

    public async Task<SessionOutcome> RunAsync(Task<SessionOutcome> operation, CancellationToken cancellationToken)
    {
        var terminal = DeliveryDiagnosticOutcome.Exception;
        long? resultAt = null;
        double deferredMilliseconds = 0;
        int? errorHResult = null;
        TextDeliveryReason? reason = null;
        double? verificationMilliseconds = null;
        try
        {
            var outcome = await operation.ConfigureAwait(false);
            if (diagnostic is not null) resultAt = outcome.DiagnosticResultTimestamp ?? Stopwatch.GetTimestamp();
            cancellationToken.ThrowIfCancellationRequested();
            if (outcome.Kind != SessionOutcomeKind.Completed)
            {
                terminal = DeliveryDiagnosticOutcome.RecognitionNotCompleted;
                return outcome;
            }
            if (!isCurrent()) { terminal = DeliveryDiagnosticOutcome.Stale; return outcome; }
            var speech = outcome.Result?.SpeechText;
            if (speech is null)
            {
                Error = "The model did not return authoritative speech text.";
                terminal = DeliveryDiagnosticOutcome.NoAuthoritativeSpeech;
                return outcome;
            }
            if (string.IsNullOrWhiteSpace(speech)) { terminal = DeliveryDiagnosticOutcome.EmptySpeech; return outcome; }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isCurrent()) { terminal = DeliveryDiagnosticOutcome.Stale; return outcome; }
                if (!typingDeferred())
                {
                    TextDeliveryOutcome delivery;
                    try { delivery = await insert(speech, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error) { Error = error.Message; errorHResult = error.HResult; return outcome; }
                    if (delivery.Status != TextDeliveryStatus.Deferred)
                    {
                        Delivery = delivery;
                        reason = delivery.Reason;
                        verificationMilliseconds = delivery.VerificationMilliseconds;
                        Sent = delivery.Status is TextDeliveryStatus.Sent or TextDeliveryStatus.Verified;
                        if (delivery.Status is TextDeliveryStatus.Rejected or TextDeliveryStatus.Partial or TextDeliveryStatus.CleanupFailed)
                            Error = delivery.Message;
                        terminal = delivery.Status switch
                        {
                            TextDeliveryStatus.Sent => DeliveryDiagnosticOutcome.Sent,
                            TextDeliveryStatus.Verified => DeliveryDiagnosticOutcome.Verified,
                            TextDeliveryStatus.Rejected => DeliveryDiagnosticOutcome.Rejected,
                            TextDeliveryStatus.Partial => DeliveryDiagnosticOutcome.Partial,
                            TextDeliveryStatus.CleanupFailed => DeliveryDiagnosticOutcome.CleanupFailed,
                            _ => DeliveryDiagnosticOutcome.Exception
                        };
                        return outcome;
                    }
                }
                var deferredAt = diagnostic is null ? 0 : Stopwatch.GetTimestamp();
                try { await Task.Delay(40, cancellationToken).ConfigureAwait(false); }
                finally
                {
                    if (diagnostic is not null) deferredMilliseconds += Stopwatch.GetElapsedTime(deferredAt).TotalMilliseconds;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            terminal = DeliveryDiagnosticOutcome.Cancelled;
            throw;
        }
        catch (Exception error)
        {
            errorHResult = error.HResult;
            throw;
        }
        finally
        {
            if (diagnostic is not null)
            {
                var duration = resultAt is { } start ? Stopwatch.GetElapsedTime(start).TotalMilliseconds : (double?)null;
                var sendDuration = terminal is DeliveryDiagnosticOutcome.Sent or DeliveryDiagnosticOutcome.Verified
                    ? duration - (verificationMilliseconds ?? 0) : null;
                try { diagnostic(new(terminal, duration, deferredMilliseconds, errorHResult, reason,
                    sendDuration, verificationMilliseconds)); }
                catch { /* Diagnostics cannot affect delivery. */ }
            }
        }
    }
}
