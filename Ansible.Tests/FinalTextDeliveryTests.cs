using Ansible.Core;

namespace Ansible.Tests;

public sealed class FinalTextDeliveryTests
{
    private static SessionOutcome Completed(string? speech) =>
        new(SessionOutcomeKind.Completed, new RecognitionResult("Speaker 0: display", speech));

    [Fact]
    public async Task VibeVoiceFinalInsertsCleanedSpeechOnce()
    {
        var result = NativeTranscript.Normalize("vibevoice_asr_streaming",
            "Speaker0: Hello there. Speaker1: Good morning.", [], []);
        var inserted = new List<string>();
        var delivery = new FinalTextDelivery((text, _) =>
        {
            inserted.Add(text);
            return Task.FromResult(TextDeliveryOutcome.Sent("ok"));
        }, () => true, () => false);
        await delivery.RunAsync(Task.FromResult(new SessionOutcome(SessionOutcomeKind.Completed, result)), CancellationToken.None);
        Assert.Equal(["Hello there. Good morning."], inserted);
        Assert.True(delivery.Sent);
    }

    [Fact]
    public async Task WaitsForRecognitionThenInsertsOnlyTheAuthoritativeFinalText()
    {
        var operation = new TaskCompletionSource<SessionOutcome>();
        var inserted = new List<string>();
        var delivery = new FinalTextDelivery((text, _) =>
        {
            inserted.Add(text);
            return Task.FromResult(TextDeliveryOutcome.Sent("ok"));
        }, () => true, () => false);

        var pending = delivery.RunAsync(operation.Task, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Empty(inserted);
        operation.SetResult(Completed("Revised final words 👋\nمرحبا"));
        await pending;

        Assert.Equal(["Revised final words 👋\nمرحبا"], inserted);
        Assert.True(delivery.Sent);
    }

    [Theory]
    [InlineData(SessionOutcomeKind.Cancelled)]
    [InlineData(SessionOutcomeKind.Failed)]
    [InlineData(SessionOutcomeKind.TimedOut)]
    public async Task UnsuccessfulRecognitionNeverInsertsEvenWhenItContainsText(SessionOutcomeKind kind)
    {
        var calls = 0;
        var delivery = new FinalTextDelivery((_, _) =>
        {
            calls++;
            return Task.FromResult(TextDeliveryOutcome.Sent("ok"));
        }, () => true, () => false);
        await delivery.RunAsync(Task.FromResult(new SessionOutcome(kind, new("unfinished", "unfinished"))), CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.False(delivery.Sent);
    }

    [Fact]
    public async Task HeldShortcutAndTransientModifierDeferTheWholeFinalText()
    {
        var held = true;
        var calls = new List<string>();
        var delivery = new FinalTextDelivery((text, _) =>
        {
            calls.Add(text);
            return Task.FromResult(calls.Count == 1
                ? TextDeliveryOutcome.Deferred("modifier held") : TextDeliveryOutcome.Sent("ok"));
        }, () => true, () => held);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = delivery.RunAsync(Task.FromResult(Completed("whole final text")), timeout.Token);
        Assert.Empty(calls);
        Assert.False(pending.IsCompleted);
        held = false;
        await pending;
        Assert.Equal(["whole final text", "whole final text"], calls);
        Assert.True(delivery.Sent);
    }

    [Fact]
    public async Task CancellationWhileDeferredDoesNotInsert()
    {
        var calls = 0;
        var delivery = new FinalTextDelivery((_, _) =>
        {
            calls++;
            return Task.FromResult(TextDeliveryOutcome.Sent("ok"));
        }, () => true, () => true);
        using var cancellation = new CancellationTokenSource();
        var pending = delivery.RunAsync(Task.FromResult(Completed("speech")), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SupersededOperationDoesNotInsert()
    {
        var calls = 0;
        var current = true;
        var delivery = new FinalTextDelivery((_, _) =>
        {
            calls++;
            current = false;
            return Task.FromResult(TextDeliveryOutcome.Deferred("modifier held"));
        }, () => current, () => false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await delivery.RunAsync(Task.FromResult(Completed("speech")), timeout.Token);
        Assert.Equal(1, calls);
        Assert.False(delivery.Sent);
    }

    [Fact]
    public async Task RejectedOrPartiallyDeliveredInsertionIsNeverRetried()
    {
        var calls = 0;
        var delivery = new FinalTextDelivery((_, _) =>
        {
            calls++;
            return Task.FromResult(TextDeliveryOutcome.Rejected("Only part of the text was inserted."));
        }, () => true, () => false);
        await delivery.RunAsync(Task.FromResult(Completed("speech")), CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("Only part of the text was inserted.", delivery.Error);
        Assert.False(delivery.Sent);
    }

    [Theory]
    [InlineData(null, "The model did not return authoritative speech text.")]
    [InlineData("", null)]
    [InlineData("  ", null)]
    public async Task MissingSpeechNeverInsertsDisplayLabels(string? speech, string? error)
    {
        var calls = 0;
        var delivery = new FinalTextDelivery((_, _) =>
        {
            calls++;
            return Task.FromResult(TextDeliveryOutcome.Sent("ok"));
        }, () => true, () => false);
        await delivery.RunAsync(Task.FromResult(Completed(speech)), CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(error, delivery.Error);
    }
}
