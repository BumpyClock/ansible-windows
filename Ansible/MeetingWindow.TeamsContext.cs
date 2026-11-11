using Ansible.Core;
using Ansible.Core.Windows;

namespace Ansible;

public sealed partial class MeetingWindow
{
    private readonly IMeetingContextReader _teamsContext = new TeamsMeetingContextReader();
    private CancellationTokenSource? _contextStop;
    private Task _contextTask = Task.CompletedTask;

    private void FollowTeamsCall(MeetingDetectionCandidate identity, MeetingContextObservation initial)
    {
        var cancellation = _contextStop = new CancellationTokenSource();
        _contextTask = FollowAsync();
        async Task FollowAsync()
        {
            try
            {
                await MeetingContextMonitor.FollowAsync(_teamsContext, identity, initial,
                    _session.AdoptTeamsTitleAsync, _session.EndAsync,
                    ReportTeamsMonitoringAvailability, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                // Optional context must not interrupt local capture or lose its transcript.
                System.Diagnostics.Trace.TraceWarning("Ansible: Teams call monitoring unavailable. HRESULT: {0:X8}.", error.HResult);
                ReportTeamsMonitoringAvailability(false);
            }
        }
    }

    private void ReportTeamsMonitoringAvailability(bool available)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed && _session.IsActive)
                _notices.Set("teams-context", available ? null : new NoticeContent(NoticeSeverity.Warning,
                    "Teams call could not be followed", "Use End meeting to finish this transcript when the call ends.",
                    IsClosable: false));
        });
    }

    private async Task StopFollowingTeamsAsync()
    {
        _contextStop?.Cancel();
        await _contextTask;
        _contextStop?.Dispose();
        _contextStop = null;
    }
}
