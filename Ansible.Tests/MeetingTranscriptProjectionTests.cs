using System.Collections.Specialized;
using Ansible;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingTranscriptProjectionTests
{
    [Fact]
    public void AppendingSameSpeakerExtendsOneBlockInsteadOfExposingRecognitionChunks()
    {
        var first = Turn("speaker_0", 0, "First speech.");
        var document = new MeetingDocument { Turns = [first] };
        var projection = new MeetingTranscriptProjection();
        projection.Reconcile(document);
        var row = projection.Rows[0];
        var properties = new List<string?>();
        row.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        Assert.True(projection.Reconcile(document with { Turns = [first with { }, Turn("speaker_0", 2, "Next speech.")] }));

        Assert.Same(row, Assert.Single(projection.Rows));
        Assert.Equal("First speech. Next speech.", row.Text);
        Assert.Contains(nameof(MeetingTranscriptRow.Text), properties);
        Assert.Equal(1, properties.Count(property => property == nameof(MeetingTranscriptRow.Text)));
    }

    [Fact]
    public void NewSpeakerStartsNewBlockAndDoesNotChangeCompletedText()
    {
        var first = Turn("speaker_0", 0, "First speech.");
        var projection = new MeetingTranscriptProjection();
        projection.Reconcile(new() { Turns = [first] });
        var row = projection.Rows[0];
        var properties = new List<string?>();
        row.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        var changes = new List<NotifyCollectionChangedAction>();
        projection.Rows.CollectionChanged += (_, args) => changes.Add(args.Action);

        projection.Reconcile(new() { Turns = [first with { }, Turn("speaker_1", 2, "Reply.")] });

        Assert.Same(row, projection.Rows[0]);
        Assert.Empty(properties);
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
        Assert.Equal("speaker_1", projection.Rows[1].SpeakerId);
        Assert.Equal("Reply.", projection.Rows[1].Text);
    }

    [Fact]
    public void RevisingSpeakerBoundarySplitsExistingBlockWithoutLosingSpeech()
    {
        var first = Turn("speaker_0", 0, "First speech.");
        var second = Turn("speaker_0", 2, "Second speech.");
        var document = new MeetingDocument { Turns = [first, second] };
        var projection = new MeetingTranscriptProjection();
        projection.Reconcile(document);
        var row = Assert.Single(projection.Rows);

        projection.Reconcile(document with { Turns = [first with { SpeakerId = "speaker_1", IsProvisional = true }, second] });

        Assert.Equal(2, projection.Rows.Count);
        Assert.Same(row, projection.Rows[0]);
        Assert.Equal("speaker_1", row.SpeakerId);
        Assert.Equal("First speech.", row.Text);
        Assert.Equal("speaker_0", projection.Rows[1].SpeakerId);
        Assert.Equal("Second speech.", projection.Rows[1].Text);
        Assert.Contains("provisional", row.TimingText);
    }

    [Fact]
    public void CorrectedSpeakerMergesAdjacentBlocksButNotSeparatedOccurrences()
    {
        var first = Turn("speaker_0", 0, "First speech.");
        var second = Turn("speaker_1", 2, "Second speech.");
        var third = Turn("speaker_0", 4, "Third speech.");
        var projection = new MeetingTranscriptProjection();
        projection.Reconcile(new() { Turns = [first, second, third] });
        Assert.Equal(3, projection.Rows.Count);
        var row = projection.Rows[0];

        projection.Reconcile(new() { Turns = [first, second with { SpeakerId = "speaker_0" }, third] });

        Assert.Same(row, Assert.Single(projection.Rows));
        Assert.Equal("First speech. Second speech. Third speech.", row.Text);
    }

    [Fact]
    public void CorrectedWordingNotifiesOnlyChangedRowAndTailRemovalDoesNotResetTheList()
    {
        var first = Turn("speaker_0", 0, "Wrong.");
        var second = Turn("speaker_1", 2, "Keep this.");
        var projection = new MeetingTranscriptProjection();
        var document = new MeetingDocument { Turns = [first, second, Turn("speaker_2", 4, "Remove this.")] };
        projection.Reconcile(document);
        var retained = projection.Rows.Take(2).ToArray();
        var changed = new List<string?>();
        var unchanged = new List<string?>();
        retained[0].PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        retained[1].PropertyChanged += (_, args) => unchanged.Add(args.PropertyName);
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        projection.Rows.CollectionChanged += (_, args) => collectionChanges.Add(args.Action);

        projection.Reconcile(document with { Turns = [first with { Text = "Correct." }, second] });

        Assert.Equal(2, projection.Rows.Count);
        Assert.Same(retained[0], projection.Rows[0]);
        Assert.Same(retained[1], projection.Rows[1]);
        Assert.Equal("Correct.", retained[0].Text);
        Assert.Equal([nameof(MeetingTranscriptRow.Text)], changed);
        Assert.Empty(unchanged);
        Assert.Equal([NotifyCollectionChangedAction.Remove], collectionChanges);
    }

    [Fact]
    public void RenameAndAutomaticNamesRefreshPriorRowsAndKeepOriginalWordingExpanded()
    {
        var turn = Turn("speaker_0", 0, "Edited.") with { OriginalText = "Original." };
        var document = new MeetingDocument { Turns = [turn] };
        var projection = new MeetingTranscriptProjection();
        projection.Reconcile(document);
        var row = projection.Rows[0];
        row.IsOriginalExpanded = true;

        document = document with { AutoSpeakerNames = new Dictionary<string, string> { ["speaker_0"] = "Automatic name" } };
        projection.Reconcile(document);
        Assert.Equal("Automatic name", row.SpeakerName);
        document = document with { SpeakerNames = new Dictionary<string, string> { ["speaker_0"] = "Chosen name" } };
        projection.Reconcile(document);

        Assert.Equal("Chosen name", row.SpeakerName);
        Assert.Equal("Rename speaker Chosen name", row.RenameSpeakerName);
        Assert.True(row.IsOriginalExpanded);
        Assert.Equal("Original.", row.OriginalText);
        Assert.True(row.HasCleanup);
        Assert.False(projection.Reconcile(document with { }));
    }

    [Fact]
    public void CleanupChangesPreserveExpansionButDifferentSpeechAtReusedPositionResetsIt()
    {
        var turn = Turn("speaker_0", 0, "Edited.") with { OriginalText = "Original." };
        var projection = new MeetingTranscriptProjection();
        var document = new MeetingDocument { Turns = [turn] };
        projection.Reconcile(document);
        var row = projection.Rows[0];
        row.IsOriginalExpanded = true;

        projection.Reconcile(document with { Turns = [turn with { Text = "Further edited.", SpeakerId = "speaker_1" }] });
        Assert.True(row.IsOriginalExpanded);
        Assert.Equal("Original.", row.OriginalText);

        projection.Reconcile(document with { Turns = [Turn("speaker_1", 2, "Different speech.") with { OriginalText = "Different original." }] });
        Assert.Same(row, projection.Rows[0]);
        Assert.False(row.IsOriginalExpanded);
    }

    [Fact]
    public void EmptyTranscriptRemovesRowsAndRetainedTextShowsTimingQuality()
    {
        var projection = new MeetingTranscriptProjection();
        var turn = Turn("speaker_0", 62, "Speech.") with { TimingQuality = MeetingTimingQuality.Aligned };
        projection.Reconcile(new MeetingDocument { Turns = [turn] });
        Assert.Equal("1:02 · ASR-aligned", projection.Rows[0].TimingText);
        Assert.Equal("speaker_0", projection.Rows[0].SpeakerName);

        Assert.True(projection.Reconcile(new MeetingDocument()));
        Assert.Empty(projection.Rows);
        Assert.False(projection.Reconcile(new MeetingDocument()));
    }

    [Fact]
    public void LongTranscriptAppendOnlyAddsTheNewRowAndSpeakerCorrectionUpdatesOneHeading()
    {
        var turns = Enumerable.Range(0, 1_800)
            .Select(index => Turn($"speaker_{index % 2}", index * 4, $"Speech number {index}."))
            .ToArray();
        var document = new MeetingDocument { Turns = turns };
        var projection = new MeetingTranscriptProjection();
        projection.Reconcile(document);
        var retained = projection.Rows.ToArray();
        var changedRows = new List<MeetingTranscriptRow>();
        foreach (var row in retained) row.PropertyChanged += (_, _) => changedRows.Add(row);

        var appended = document with { Turns = [.. turns, Turn("speaker_0", 7_200, "New speech.")] };
        projection.Reconcile(appended);
        Assert.Empty(changedRows);
        Assert.Equal(1_801, projection.Rows.Count);
        for (var index = 0; index < retained.Length; index++) Assert.Same(retained[index], projection.Rows[index]);

        var corrected = appended.Turns.ToArray();
        corrected[100] = corrected[100] with { SpeakerId = "speaker_2" };
        projection.Reconcile(appended with { Turns = corrected });
        Assert.NotEmpty(changedRows);
        Assert.All(changedRows, row => Assert.Same(retained[100], row));
        Assert.Equal("speaker_2", projection.Rows[100].SpeakerName);
    }

    [Fact]
    public void ContinuousMonologueHasOneNamedBlockTimeRangeAndOriginalAcrossPartialCleanup()
    {
        var first = Turn("Speaker 1", 65, "First edited sentence.") with
        { OriginalText = "first raw sentence", TimingQuality = MeetingTimingQuality.Aligned };
        var second = Turn("Speaker 1", 75, "second sentence") with { TimingQuality = MeetingTimingQuality.Aligned };
        var third = Turn("Speaker 1", 85, "third edited sentence") with
        { OriginalText = "third raw sentence", TimingQuality = MeetingTimingQuality.Aligned };
        var document = new MeetingDocument { Turns = [first, second, third] };
        var projection = new MeetingTranscriptProjection();
        projection.Reconcile(document);
        var row = Assert.Single(projection.Rows);

        Assert.Equal("Speaker 1", row.SpeakerName);
        Assert.Equal("First edited sentence. second sentence third edited sentence", row.Text);
        Assert.Equal("first raw sentence second sentence third raw sentence", row.OriginalText);
        Assert.Equal("1:05 to 1:27 · ASR-aligned · wording edited", row.TimingText);
        Assert.True(row.HasCleanup);
        Assert.Equal(3, document.Turns.Count);
        Assert.Equal("first raw sentence", document.Turns[0].OriginalText);

        row.IsOriginalExpanded = true;
        var fourth = Turn("Speaker 1", 95, "live tail") with { TimingQuality = MeetingTimingQuality.Aligned, IsProvisional = true };
        projection.Reconcile(document with { Turns = [first, second, third, fourth] });
        Assert.Same(row, Assert.Single(projection.Rows));
        Assert.True(row.IsOriginalExpanded);
        Assert.EndsWith("third raw sentence live tail", row.OriginalText);
        Assert.Contains("provisional", row.TimingText);
        Assert.Contains("to 1:37", row.TimingText);
    }

    [Fact]
    public void BlockDoesNotClaimAlignedTimingWhenAnyConstituentTurnIsUnaligned()
    {
        var first = Turn("Speaker 1", 0, "First.") with { TimingQuality = MeetingTimingQuality.Aligned };
        var second = Turn("Speaker 1", 2, "Second.") with { TimingQuality = MeetingTimingQuality.Chunk };
        var projection = new MeetingTranscriptProjection();
        projection.Reconcile(new() { Turns = [first, second] });
        var row = Assert.Single(projection.Rows);
        Assert.Contains("approximate chunk time", row.TimingText);
        Assert.DoesNotContain("ASR-aligned", row.TimingText);
        projection.Reconcile(new() { Turns = [first, second with { TimingQuality = MeetingTimingQuality.Unknown }] });
        Assert.Equal("Time unavailable", row.TimingText);
    }

    private static MeetingTurn Turn(string speaker, double start, string text) => new(speaker, start, start + 2, text);
}
