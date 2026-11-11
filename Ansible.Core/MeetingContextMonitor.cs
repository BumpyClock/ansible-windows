using System.Diagnostics;

namespace Ansible.Core;

/// <summary>Follows a selected Teams call without treating silence or an unreadable UI as its end.</summary>
public static class MeetingContextMonitor
{
    public static async Task FollowAsync(IMeetingContextReader reader, MeetingDetectionCandidate identity,
        MeetingContextObservation initial, Func<string, Task> adoptTitle, Func<Task> endMeeting,
        CancellationToken token)
    {
        var tracker = new MeetingCallEndTracker();
        var started = Stopwatch.GetTimestamp();
        var observation = initial;
        string? adoptedTitle = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (observation.Presence == MeetingCallPresence.Active && observation.Title is { } title && title != adoptedTitle)
            {
                await adoptTitle(title).ConfigureAwait(false);
                adoptedTitle = title;
            }
            if (tracker.Observe(observation.Presence, Stopwatch.GetElapsedTime(started)))
            {
                token.ThrowIfCancellationRequested();
                await endMeeting().ConfigureAwait(false);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            observation = await reader.ReadAsync(identity, token).ConfigureAwait(false);
        }
    }
}

internal sealed class MeetingCallEndTracker
{
    private bool _observedCall;
    private TimeSpan? _absentSince;
    private int _absentReads;

    internal bool Observe(MeetingCallPresence presence, TimeSpan now)
    {
        if (presence == MeetingCallPresence.Active) _observedCall = true;
        if (!_observedCall || presence != MeetingCallPresence.Absent)
        {
            _absentSince = null;
            _absentReads = 0;
            return false;
        }
        _absentSince ??= now;
        _absentReads++;
        return _absentReads >= 3 && now - _absentSince.Value >= TimeSpan.FromSeconds(5);
    }
}
