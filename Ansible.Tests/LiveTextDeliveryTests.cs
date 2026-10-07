using Ansible.Core;

namespace Ansible.Tests;

public sealed class LiveTextDeliveryTests
{
    private sealed class Recorder
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _attempts = new(0);
        private readonly List<string> _inserted = [];

        public List<(NoticeKind Kind, string Title, string Message)> Notices { get; } = [];
        public Func<string, TextDeliveryOutcome> Respond { get; set; } = _ => TextDeliveryOutcome.Sent("ok");
        public bool Current { get; set; } = true;
        public bool TypingDeferred { get; set; }

        public Task<TextDeliveryOutcome> InsertAsync(string pending, CancellationToken token)
        {
            var outcome = Respond(pending);
            lock (_sync)
            {
                if (outcome.Status == TextDeliveryStatus.Sent) { _inserted.Add(pending); }
            }
            _attempts.Release();
            return Task.FromResult(outcome);
        }

        public void Notify(NoticeKind kind, string title, string message)
        {
            lock (_sync) { Notices.Add((kind, title, message)); }
        }

        public async Task WaitForAttemptsAsync(int count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            for (var i = 0; i < count; i++) { await _attempts.WaitAsync(cts.Token); }
        }

        public string Typed { get { lock (_sync) { return string.Concat(_inserted); } } }
    }

    private static LiveTextDelivery Create(Recorder recorder) =>
        new(recorder.InsertAsync, () => recorder.Current, () => recorder.TypingDeferred, recorder.Notify);

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static void Publish(LiveTextDelivery delivery, string speech) =>
        delivery.Report(new TranscriptUpdate(speech, false, speech));

    [Fact]
    public async Task TypesOnlyThePendingRemainderAsPartialsArrive()
    {
        var recorder = new Recorder();
        var delivery = Create(recorder);
        using var cts = new CancellationTokenSource(Guard);
        var pump = delivery.RunAsync(cts.Token);

        Publish(delivery, "he");
        await recorder.WaitForAttemptsAsync(1, Short);
        Publish(delivery, "hello");
        await recorder.WaitForAttemptsAsync(1, Short);

        delivery.Complete();
        await pump;

        Assert.Equal("hello", recorder.Typed);
        Assert.False(delivery.Stopped);
    }

    [Fact]
    public async Task ReleaseFlushesRetainedTextWithoutAnyLaterSpeechUpdate()
    {
        var recorder = new Recorder { TypingDeferred = true };
        var delivery = Create(recorder);
        using var cts = new CancellationTokenSource(Guard);
        var pump = delivery.RunAsync(cts.Token);

        Publish(delivery, "hello");
        await Task.Delay(100, cts.Token);
        Assert.Equal("", recorder.Typed); // held modifier delays typing

        recorder.TypingDeferred = false;
        delivery.SignalRelease(); // release wakes the sole consumer; no new speech arrives
        await recorder.WaitForAttemptsAsync(1, Short);

        delivery.Complete();
        await pump;

        Assert.Equal("hello", recorder.Typed);
        Assert.False(delivery.Stopped);
    }

    [Fact]
    public async Task ATransientModifierDefersTheFinalFlushThenInserts()
    {
        var attempts = 0;
        var recorder = new Recorder
        {
            Respond = _ => ++attempts == 1 ? TextDeliveryOutcome.Deferred("held") : TextDeliveryOutcome.Sent("ok")
        };
        var delivery = Create(recorder);
        using var cts = new CancellationTokenSource(Guard);

        await delivery.InsertFinalAsync("hello", cts.Token);

        Assert.Equal("hello", recorder.Typed);
        Assert.False(delivery.Stopped);
        Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task ARejectionStopsTheDeliveryAndNotifiesOnce()
    {
        var recorder = new Recorder { Respond = _ => TextDeliveryOutcome.Rejected("focus lost") };
        var delivery = Create(recorder);
        using var cts = new CancellationTokenSource(Guard);
        var pump = delivery.RunAsync(cts.Token);

        Publish(delivery, "hello");
        await recorder.WaitForAttemptsAsync(1, Short);

        delivery.Complete();
        await pump;

        Assert.True(delivery.Stopped);
        Assert.Equal("focus lost", delivery.Error);
        Assert.Equal("", recorder.Typed);
        Assert.Contains(recorder.Notices, notice => notice.Title == "Insertion stopped" && notice.Message == "focus lost");
    }

    [Fact]
    public async Task ARevisionOfAlreadyTypedTextStopsTheDelivery()
    {
        var recorder = new Recorder();
        var delivery = Create(recorder);
        using var cts = new CancellationTokenSource(Guard);
        var pump = delivery.RunAsync(cts.Token);

        Publish(delivery, "hello");
        await recorder.WaitForAttemptsAsync(1, Short);
        Publish(delivery, "world"); // not a prefix extension of already-sent "hello"
        delivery.Complete();
        await pump;

        Assert.True(delivery.Stopped);
        Assert.Equal("hello", recorder.Typed);
        Assert.Contains(recorder.Notices, notice => notice.Title == "Insertion stopped");
    }

    [Fact]
    public async Task CancellationCompletesThePumpSoTheOwnerCanJoinOnClose()
    {
        var recorder = new Recorder();
        var delivery = Create(recorder);
        using var cts = new CancellationTokenSource();
        var pump = delivery.RunAsync(cts.Token);

        cts.Cancel();
        await pump.WaitAsync(Guard);

        Assert.True(pump.IsCompletedSuccessfully);
        Assert.False(delivery.Stopped);
    }

    [Fact]
    public async Task TheFinalSpeechInsertsOnlyTheRemainderAfterLivePartials()
    {
        var recorder = new Recorder();
        var delivery = Create(recorder);
        using var cts = new CancellationTokenSource(Guard);
        var pump = delivery.RunAsync(cts.Token);

        Publish(delivery, "hello");
        await recorder.WaitForAttemptsAsync(1, Short);
        delivery.Complete();
        await pump;

        await delivery.InsertFinalAsync("hello world", cts.Token);

        Assert.Equal("hello world", recorder.Typed);
        Assert.False(delivery.Stopped);
    }

    [Fact]
    public async Task ANullFinalSpeechMarksStoppedWithoutANotice()
    {
        var recorder = new Recorder();
        var delivery = Create(recorder);

        await delivery.InsertFinalAsync(null, CancellationToken.None);

        Assert.True(delivery.Stopped);
        Assert.Equal("The model did not return authoritative speech text.", delivery.Error);
        Assert.Empty(recorder.Notices);
    }
}
