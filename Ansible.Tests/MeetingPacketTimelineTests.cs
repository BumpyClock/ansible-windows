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
    public void ContiguousDeviceFramesDoNotBecomeOverlapsWhenQpcJittersBeyondFiveMilliseconds()
    {
        var clock = new MeetingPacketClock();
        Assert.Equal(2501, clock.Map(2501, 19800000, 160));
        Assert.Equal(2661, clock.Map(2739, 19800160, 160));
        Assert.Equal(2821, clock.Map(2739, 19800320, 160));
        Assert.Equal(3400, clock.Map(3400, 19800900, 160));
    }
    [Fact]
    public void NativeRatePositionsAndUnavailableCountersKeepTimestampBasedGaps()
    {
        var nativeRate = new MeetingPacketClock();
        Assert.Equal(0, nativeRate.Map(0, 480, 160));
        Assert.Equal(160, nativeRate.Map(139, 960, 160));
        Assert.Equal(480, nativeRate.Map(480, 1920, 160));
        var unavailable = new MeetingPacketClock();
        Assert.Equal(0, unavailable.Map(0, 0, 160));
        Assert.Equal(160, unavailable.Map(139, 0, 160));
        Assert.Equal(480, unavailable.Map(480, 0, 160));
    }
    [Fact]
    public void StartupSamplesBeforeTheCaptureOriginAreExcluded()
    {
        var timeline = new MeetingPacketTimeline();
        timeline.Add(-1, new byte[] { 1, 0, 2, 0 }, 1);
        Assert.Equal(new byte[] { 2, 0 }, timeline.Read(1));
    }
}
