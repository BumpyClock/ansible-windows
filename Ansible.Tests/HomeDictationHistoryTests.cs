using Ansible.Core;

namespace Ansible;

public sealed class HomeDictationHistoryTests : IDisposable
{
    private readonly HomeDictationHistory _history = new();

    public void Dispose() => _history.Dispose();

    [Fact]
    public void AddIsIdempotentAndNewestItemsComeFirst()
    {
        var first = Completion(1, "first");
        var second = Completion(2, "second");

        Assert.True(_history.Add(first));
        Assert.False(_history.Add(first));
        Assert.True(_history.Add(second));

        var items = _history.Snapshot();
        Assert.Equal(2, items.Count);
        Assert.Equal(second.OperationId, items[0].OperationId);
        Assert.Equal(first.OperationId, items[1].OperationId);
    }

    [Fact]
    public void InstancesDoNotShareHistory()
    {
        using var other = new HomeDictationHistory();

        Assert.True(_history.Add(Completion(3, "only in first instance")));

        Assert.Empty(other.Snapshot());
    }

    [Fact]
    public void HistoryIsBoundedByCountAndTranscriptCharacters()
    {
        for (var index = 0; index < 40; index++)
            Assert.True(_history.Add(Completion(index, new string('x', 20_000))));

        var items = _history.Snapshot();
        Assert.InRange(items.Count, 1, 100);
        Assert.True(items.Sum(item => item.Text.Length) <= 500_000);
        Assert.Equal((byte)39, items[0].OperationId.ToByteArray()[15]);
    }

    [Fact]
    public void HistoryKeepsOnlyTheNewestHundredItems()
    {
        for (var index = 0; index < 120; index++)
            Assert.True(_history.Add(Completion(index, "short")));

        var items = _history.Snapshot();
        Assert.Equal(100, items.Count);
        Assert.Equal((byte)119, items[0].OperationId.ToByteArray()[15]);
        Assert.Equal((byte)20, items[^1].OperationId.ToByteArray()[15]);
    }

    [Fact]
    public void OversizedTranscriptIsRejectedWithoutChangingItsText()
    {
        var item = Completion(7, new string('x', HomeDictationHistory.MaximumCharacters + 1));

        Assert.False(_history.Add(item));
        Assert.Empty(_history.Snapshot());
        Assert.True(_history.OversizedCurrentResult);
        Assert.False(_history.Contains(item.OperationId));
    }

    [Fact]
    public void RemoveDropsTheItemOnceAndNotifies()
    {
        var first = Completion(1, "first");
        var second = Completion(2, "second");
        _history.Add(first);
        _history.Add(second);
        var changes = 0;
        _history.Changed += () => changes++;

        Assert.True(_history.Remove(first.OperationId));
        Assert.False(_history.Remove(first.OperationId));

        Assert.Equal(1, changes);
        Assert.Equal(second.OperationId, Assert.Single(_history.Snapshot()).OperationId);
        Assert.False(_history.Contains(first.OperationId));
    }

    [Fact]
    public void RecordDeliveryUpdatesTheRetainedItemAndNotifiesOnlyOnChange()
    {
        var item = Completion(4, "delivered");
        _history.Add(item);
        var changes = 0;
        _history.Changed += () => changes++;

        Assert.True(_history.RecordDelivery(item.OperationId, TextDeliveryStatus.Sent));
        Assert.False(_history.RecordDelivery(item.OperationId, TextDeliveryStatus.Sent));
        Assert.True(_history.RecordDelivery(item.OperationId, TextDeliveryStatus.Verified));

        Assert.Equal(2, changes);
        Assert.Equal(TextDeliveryStatus.Verified, Assert.Single(_history.Snapshot()).Delivery);
    }

    [Fact]
    public void RecordDeliveryIgnoresDictationsThatHistoryDoesNotHold()
    {
        var oversized = Completion(5, new string('x', HomeDictationHistory.MaximumCharacters + 1));
        _history.Add(oversized);
        var changes = 0;
        _history.Changed += () => changes++;

        Assert.False(_history.RecordDelivery(oversized.OperationId, TextDeliveryStatus.Verified));
        Assert.False(_history.RecordDelivery(GuidFrom(6), TextDeliveryStatus.Verified));

        Assert.Equal(0, changes);
        Assert.Empty(_history.Snapshot());
    }

    [Fact]
    public void NewItemsHaveNoDeliveryUntilOneIsRecorded()
    {
        Assert.True(_history.Add(Completion(8, "in app")));

        Assert.Null(Assert.Single(_history.Snapshot()).Delivery);
    }

    private static DictationCompletion Completion(int seed, string text) => new(
        GuidFrom(seed), "model", new RecognitionResult(text, text), DateTimeOffset.UtcNow.AddSeconds(seed), TimeSpan.FromSeconds(seed));

    private static Guid GuidFrom(int seed)
    {
        var bytes = new byte[16];
        bytes[15] = checked((byte)seed);
        return new Guid(bytes);
    }
}
