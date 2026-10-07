using Ansible.Core;

namespace Ansible.Tests;

// Permanent regressions for two contract bugs in the live-delivery consumer. Both failed on the prior design
// (a null-speech wake sentinel written into the capacity-one data channel, and a live pump that ignored a
// genuine Deferred and blocked for the next channel item). Adapted to the encapsulated Report/Complete API.
public sealed class LiveTextDeliveryRegressionTests
{
    [Fact]
    public async Task ReleaseCannotEvictUnconsumedCumulativeSpeech()
    {
        var typed = "";
        var delivery = new LiveTextDelivery(
            (pending, _) => { typed += pending; return Task.FromResult(TextDeliveryOutcome.Sent("ok")); },
            () => true,
            () => false,
            (_, _, _) => { });

        // Speech, release, and completion all arrive before the consumer runs. The release must not evict the
        // queued cumulative speech (the prior sentinel-into-data-channel design did).
        delivery.Report(new TranscriptUpdate("hello", false, "hello"));
        delivery.SignalRelease();
        delivery.Complete();

        await delivery.RunAsync(CancellationToken.None);

        Assert.Equal("hello", typed);
    }

    [Fact]
    public async Task PhysicalModifierDeferralResumesWithoutAnotherRecognitionEvent()
    {
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var typed = "";
        var delivery = new LiveTextDelivery(
            (pending, _) =>
            {
                if (++attempts == 1)
                {
                    firstAttempt.SetResult();
                    return Task.FromResult(TextDeliveryOutcome.Deferred("held"));
                }
                typed += pending;
                resumed.SetResult();
                return Task.FromResult(TextDeliveryOutcome.Sent("ok"));
            },
            () => true,
            () => false,
            (_, _, _) => { });

        using var cancellation = new CancellationTokenSource();
        var pump = delivery.RunAsync(cancellation.Token);
        try
        {
            // Exactly one recognition event; the deferral must resume via bounded retry with no later
            // transcript or release signal.
            delivery.Report(new TranscriptUpdate("hello", false, "hello"));
            await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("hello", typed);
        }
        finally
        {
            cancellation.Cancel();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
