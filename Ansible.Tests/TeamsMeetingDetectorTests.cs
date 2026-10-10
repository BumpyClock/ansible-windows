using Ansible.Core;
using Ansible.Core.Windows;
using Xunit.Abstractions;

namespace Ansible.Tests;

public sealed class TeamsMeetingDetectorTests(ITestOutputHelper output)
{
    [Fact]
    public void EmptyCaptureOwnersDoNotOfferAMeeting()
    {
        Assert.Null(TeamsMeetingDetector.SelectCandidate([]));
    }
    [Fact]
    public void MultipleCandidatesHaveDeterministicPriority()
    {
        var created = new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new(12, created), TeamsMeetingDetector.SelectCandidate([
            new(20, created.AddSeconds(1)), new(18, created), new(12, created)]));
    }
    [Fact]
    public void TeamsWebViewCaptureOwnerResolvesToItsTeamsRoot()
    {
        var observed = new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc);
        var processes = new Dictionary<int, TeamsProcessNode>
        {
            [30] = new(30, 20, "msedgewebview2", observed.AddMinutes(-1)),
            [20] = new(20, 10, "ms-teams", observed.AddMinutes(-2)),
            [10] = new(10, 0, "explorer", observed.AddHours(-1))
        };
        Assert.Equal(new MeetingDetectionCandidate(20, observed.AddMinutes(-2)),
            TeamsMeetingDetector.ResolveCaptureOwner(30, id => processes.GetValueOrDefault(id), observed));
    }
    [Fact]
    public void BrowserWithoutTeamsAncestorDoesNotOfferAMeeting()
    {
        var observed = DateTime.UtcNow;
        Assert.Null(TeamsMeetingDetector.ResolveCaptureOwner(30,
            id => id == 30 ? new(30, 0, "msedgewebview2", observed.AddMinutes(-1)) : null, observed));
    }
    [Fact]
    public void ReusedParentPidIsRejectedByCreationChronology()
    {
        var observed = DateTime.UtcNow;
        Assert.Null(TeamsMeetingDetector.ResolveCaptureOwner(30, id => id switch
        {
            30 => new(30, 20, "msedgewebview2", observed.AddMinutes(-2)),
            20 => new(20, 0, "ms-teams", observed.AddMinutes(-1)),
            _ => null
        }, observed));
    }
    [Fact]
    public void NewProcessAfterAudioObservationIsNotTheObservedOwner()
    {
        var observed = DateTime.UtcNow;
        Assert.Null(TeamsMeetingDetector.ResolveCaptureOwner(20,
            id => new(id, 0, "ms-teams", observed.AddSeconds(1)), observed));
    }
    [Fact]
    public void CyclesAndOverlongChainsDoNotGuessAnOwner()
    {
        var observed = DateTime.UtcNow;
        Assert.Null(TeamsMeetingDetector.ResolveCaptureOwner(1,
            id => new(id, id, "msedgewebview2", observed.AddMinutes(-1)), observed));
        Assert.Null(TeamsMeetingDetector.ResolveCaptureOwner(1,
            id => new(id, id + 1, id == 17 ? "ms-teams" : "msedgewebview2", observed.AddMinutes(-1)), observed));
    }
    [Fact, Trait("Category", "NativeIntegration")]
    public async Task WindowsCaptureSessionsCanBeEnumeratedWithoutRecording()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var detector = new TeamsMeetingDetector();
        var candidate = await detector.ReadAsync(timeout.Token);
        if (candidate is not null)
        {
            Assert.True(candidate.ProcessId > 0);
            Assert.Equal(DateTimeKind.Utc, candidate.ProcessStartedUtc.Kind);
        }
        output.WriteLine($"Windows capture-session enumeration completed; Teams microphone owner found: {candidate is not null}. No audio captured or personal names read.");
    }
    [Fact]
    public async Task CancellationDoesNotStartNativeEnumeration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TeamsMeetingDetector().ReadAsync(cancellation.Token));
    }
}
