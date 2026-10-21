namespace Ansible;

public sealed class HomeDictationHistoryTests : IDisposable
{
    public HomeDictationHistoryTests() => HomeDictationHistory.Reset();

    public void Dispose() => HomeDictationHistory.Reset();

    [Fact]
    public void AddIsIdempotentAndNewestItemsComeFirst()
    {
        var first = Item(1, "first");
        var second = Item(2, "second");

        Assert.True(HomeDictationHistory.Add(first));
        Assert.False(HomeDictationHistory.Add(first));
        Assert.True(HomeDictationHistory.Add(second));

        var items = HomeDictationHistory.Snapshot();
        Assert.Equal(2, items.Count);
        Assert.Equal(second.OperationId, items[0].OperationId);
        Assert.Equal(first.OperationId, items[1].OperationId);
    }

    [Fact]
    public void HistoryIsBoundedByCountAndTranscriptCharacters()
    {
        for (var index = 0; index < 40; index++)
            Assert.True(HomeDictationHistory.Add(Item(index, new string('x', 20_000))));

        var items = HomeDictationHistory.Snapshot();
        Assert.InRange(items.Count, 1, 100);
        Assert.True(items.Sum(item => item.Text.Length) <= 500_000);
        Assert.Equal((byte)39, items[0].OperationId.ToByteArray()[15]);
    }

    [Fact]
    public void HistoryKeepsOnlyTheNewestHundredItems()
    {
        for (var index = 0; index < 120; index++)
            Assert.True(HomeDictationHistory.Add(Item(index, "short")));

        var items = HomeDictationHistory.Snapshot();
        Assert.Equal(100, items.Count);
        Assert.Equal((byte)119, items[0].OperationId.ToByteArray()[15]);
        Assert.Equal((byte)20, items[^1].OperationId.ToByteArray()[15]);
    }

    [Fact]
    public void OversizedTranscriptIsRejectedWithoutChangingItsText()
    {
        var item = Item(7, new string('x', HomeDictationHistory.MaximumCharacters + 1));

        Assert.False(HomeDictationHistory.Add(item));
        Assert.Empty(HomeDictationHistory.Snapshot());
    }

    private static HomeHistoryItem Item(int seed, string text) => new(
        GuidFrom(seed), DateTimeOffset.UtcNow.AddSeconds(seed), text, "model", 1, TimeSpan.FromSeconds(seed));

    private static Guid GuidFrom(int seed)
    {
        var bytes = new byte[16];
        bytes[15] = checked((byte)seed);
        return new Guid(bytes);
    }
}
