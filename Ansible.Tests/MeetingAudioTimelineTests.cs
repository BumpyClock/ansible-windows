using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingAudioTimelineTests
{
    [Fact]
    public void RingRetainsOnlyThirtySecondsAndUsesSampleClock()
    {
        var timeline = new MeetingAudioTimeline();
        timeline.Append(new byte[16000 * 2 * 31]);
        var clip = timeline.Clip(31 * 16000)!.Value;
        Assert.Equal(16000, clip.StartSample);
        Assert.Equal(30 * 16000, clip.Audio.Samples.Length);
        Assert.Null(timeline.Clip(16000));
        Assert.Null(timeline.Clip(32 * 16000));
    }

    [Fact]
    public void PcmConversionRetainsSignedSamples()
    {
        var timeline = new MeetingAudioTimeline();
        timeline.Append([0, 128, 255, 127]);
        Assert.Equal(new[] { -1f, 32767 / 32768f }, timeline.Clip(2)!.Value.Audio.Samples);
        Assert.Throws<ArgumentException>(() => timeline.Append([1]));
    }

    [Fact]
    public void SourceRequiresFullCoverageAndExclusiveActivity()
    {
        var timeline = new MeetingAudioTimeline();
        timeline.Observe(new(0, 16000, 0, 0.2));
        Assert.Equal(MeetingAudioSource.Playback, timeline.Source(0.25, 0.75));
        Assert.Equal(MeetingAudioSource.Mixed, timeline.Source(0, 1.1));
        timeline.Observe(new(16000, 16000, 0.2, 0));
        Assert.Equal(MeetingAudioSource.Microphone, timeline.Source(1, 2));
        Assert.Equal(MeetingAudioSource.Mixed, timeline.Source(0.5, 1.5));
        timeline.Observe(new(32000, 16000, 0.1, 0.2));
        Assert.Equal(MeetingAudioSource.Mixed, timeline.Source(2, 3));
    }
}
