using Ansible.Core.Windows;

namespace Ansible.Tests;

public sealed class MeetingPacketTimelineTests
{
    [Fact]
    public void TimestampedPacketsKeepGapsInsteadOfShiftingLaterSpeech()
    {
        var timeline = new MeetingPacketTimeline();
        timeline.Add(2, new byte[] { 0xE8, 0x03, 0xD0, 0x07 }, 4);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0xE8, 0x03, 0xD0, 0x07, 0, 0 }, timeline.Read(5));
        Assert.Equal(5, timeline.Cursor);
    }
    [Fact]
    public void RingWrapDoesNotReplayOldSpeech()
    {
        var timeline = new MeetingPacketTimeline();
        timeline.Add(0, new byte[] { 1, 0 }, 0);
        timeline.Read(MeetingPacketTimeline.CapacitySamples);
        timeline.Add(MeetingPacketTimeline.CapacitySamples, new byte[] { 2, 0 }, MeetingPacketTimeline.CapacitySamples);
        Assert.Equal(new byte[] { 2, 0, 0, 0 }, timeline.Read(2));
    }
    [Fact]
    public void LateOverlappingAndFutureAudioAreRejected()
    {
        var timeline = new MeetingPacketTimeline();
        timeline.Add(0, new byte[10], 5);
        Assert.Throws<IOException>(() => timeline.Add(2, new byte[4], 5));
        timeline.Read(10);
        Assert.Throws<IOException>(() => timeline.Add(5, new byte[4], 10));
        Assert.Throws<IOException>(() => timeline.Add(1000, new byte[4], 10));
    }
    [Fact]
    public void SourceBuffersCannotGrowPastTheirBound()
    {
        var timeline = new MeetingPacketTimeline();
        Assert.Throws<IOException>(() => timeline.Add(32000, new byte[2], 32000));
        Assert.Throws<InvalidDataException>(() => timeline.Add(0, new byte[64002], 0));
    }
    [Fact]
    public void OneSampleTimestampRoundingPreservesAllSamples()
    {
        var timeline = new MeetingPacketTimeline();
        timeline.Add(0, new byte[] { 1, 0, 2, 0 }, 2);
        timeline.Add(1, new byte[] { 3, 0, 4, 0 }, 4);
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 }, timeline.Read(4));
    }
    [Fact]
    public void QpcHundredNanosecondUnitsMapToSixteenKilohertz()
    {
        Assert.Equal(16000, MeetingCaptureClock.Samples(11000000, 1000000));
        Assert.Equal(4000, MeetingCaptureClock.Samples(3500000, 1000000));
    }
    [Fact]
    public void PacketClockNormalizesCorrelationJitterButKeepsRealGaps()
    {
        var clock = new MeetingPacketClock();
        Assert.Equal(0, clock.Map(0, 0, 160));
        Assert.Equal(160, clock.Map(139, 160, 160));
        Assert.Equal(480, clock.Map(480, 480, 160));
        Assert.Throws<IOException>(() => clock.Map(640, 100, 160));
    }
    [Fact]
    public void StartupSamplesBeforeTheCaptureOriginAreExcluded()
    {
        var timeline = new MeetingPacketTimeline();
        timeline.Add(-1, new byte[] { 1, 0, 2, 0 }, 1);
        Assert.Equal(new byte[] { 2, 0 }, timeline.Read(1));
    }
}
