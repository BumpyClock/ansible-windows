namespace Ansible.Core;

/// <summary>
/// Owns one meeting run's audio, source activity, speaker observations, alignment, and naming evidence.
/// Positions are meeting stream samples.
/// </summary>
/// <remarks>
/// The session serializes capture attachment and detachment. <see cref="RefineChunkTurns"/> blocks on optional
/// alignment under the session's update lock. Observer and capture callbacks arrive on their own threads.
/// After disposal starts, refinement keeps chunk timing instead of creating another aligner, and attachment throws.
/// </remarks>
internal sealed class MeetingSpeakerEvidence : IAsyncDisposable
{
    private readonly Func<IMeetingSpeakerObserver>? _observerFactory;
    private readonly Func<IMeetingWordAligner>? _alignerFactory;
    private readonly int? _processId;
    private readonly Action<MeetingDiagnosticKind, bool, Exception?> _report;
    private readonly CancellationToken _token;
    private readonly MeetingAudioTimeline _timeline = new();
    private readonly MeetingSpeakerMatcher _matcher = new();
    private readonly object _lifecycle = new();
    private IMeetingSpeakerObserver? _observer;
    private IMeetingWordAligner? _aligner;
    private Attachment? _attachment;
    private bool _alignmentUnavailable;
    private bool _alignmentAvailabilityReported;
    private volatile bool _disposed;

    private sealed record Attachment(IMeetingAudioSourceMetadata Source, Action<MeetingAudioActivity> Activity, long Origin)
    {
        public long Position => Origin + Source.ClockSamplePosition;
    }

    public MeetingSpeakerEvidence(Func<IMeetingSpeakerObserver>? observerFactory,
        Func<IMeetingWordAligner>? alignerFactory, int? processId,
        Action<MeetingDiagnosticKind, bool, Exception?> report, CancellationToken token)
    {
        _observerFactory = observerFactory;
        _alignerFactory = alignerFactory;
        _processId = processId;
        _report = report;
        _token = token;
        if (alignerFactory is null)
        {
            _alignmentAvailabilityReported = true;
            _report(MeetingDiagnosticKind.RefinementAvailability, false, null);
        }
    }

    public bool HasResources => _aligner is not null || _observer is not null;

    public void Append(byte[] pcm) => _timeline.Append(pcm);

    /// <summary>Rebases capture evidence onto the meeting stream and reports naming availability once.</summary>
    public async Task AttachAsync(IMeetingAudioSourceMetadata capture, long originSample)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentOutOfRangeException.ThrowIfNegative(originSample);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attachment is not null) throw new InvalidOperationException("Detach the previous meeting capture first.");
        Action<MeetingAudioActivity> activity = value =>
            _timeline.Observe(value with { StartSample = originSample + value.StartSample });
        var attachment = new Attachment(capture, activity, originSample);
        capture.Activity += activity;
        _attachment = attachment;
        if (_observerFactory is null || _processId is not > 0)
        {
            _report(MeetingDiagnosticKind.NamingAvailability, false, null);
            return;
        }
        _observer ??= _observerFactory();
        try
        {
            await _observer.StartAsync(_processId.Value, () => attachment.Position, _matcher.Observe, _token);
            _report(MeetingDiagnosticKind.NamingAvailability, true, null);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            _report(MeetingDiagnosticKind.NamingAvailability, false, error);
            System.Diagnostics.Trace.TraceWarning("Ansible: automatic speaker names unavailable. {0}", error.Message);
        }
    }

    /// <summary>Stops observation, drains with activity subscribed, then detaches. A failed drain permits retry.</summary>
    public async Task DetachAsync(Func<Task> drainCapture)
    {
        ArgumentNullException.ThrowIfNull(drainCapture);
        if (_attachment is { } attachment)
        {
            if (_observer is { } observer)
            {
                try { await observer.StopAsync(); }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    _report(MeetingDiagnosticKind.NamingAvailability, false, error);
                    System.Diagnostics.Trace.TraceWarning("Ansible: automatic speaker names unavailable. {0}", error.Message);
                }
            }
            _matcher.Observe(new(attachment.Position, [], null, MeetingSpeakerReadStatus.Unavailable));
        }
        await drainCapture();
        ReleaseActivity();
    }

    /// <summary>Preserves unchanged turns, aligns chunk timing, then tags audio sources.</summary>
    public void RefineChunkTurns(MeetingTurn[] turns, IReadOnlyList<MeetingTurn> previous, long processedSamples)
    {
        for (var index = 0; index < turns.Length; index++)
        {
            var old = index < previous.Count ? previous[index] : null;
            if (old?.SpeakerId == turns[index].SpeakerId && old.Text == turns[index].Text)
                turns[index] = old with { IsProvisional = turns[index].IsProvisional };
        }
        AlignTiming(turns, processedSamples);
        TagSources(turns, previous);
    }

    /// <summary>Tags audio sources without changing recognizer-provided timing.</summary>
    public void RefineTimedTurns(MeetingTurn[] turns, IReadOnlyList<MeetingTurn> previous) => TagSources(turns, previous);

    public MeetingDocument NameSpeakers(MeetingDocument document) => _matcher.Refine(document);

    private void AlignTiming(MeetingTurn[] turns, long processedSamples)
    {
        if (_disposed || _alignerFactory is null || _alignmentUnavailable) return;
        var clip = _timeline.Clip(processedSamples);
        if (clip is null) return;
        var origin = clip.Value.StartSample / 16000.0;
        var indices = Enumerable.Range(0, turns.Length).Where(index =>
            !turns[index].IsProvisional && (turns[index].TimingQuality == MeetingTimingQuality.Aligned
                ? turns[index].EndSeconds > origin : turns[index].StartSeconds >= origin)).ToArray();
        if (!indices.Any(index => turns[index].TimingQuality != MeetingTimingQuality.Aligned)) return;
        try
        {
            IMeetingWordAligner aligner;
            lock (_lifecycle)
            {
                if (_disposed) return;
                aligner = _aligner ??= _alignerFactory();
            }
            if (!_alignmentAvailabilityReported)
            {
                aligner.InitializeAsync(_token).GetAwaiter().GetResult();
                _alignmentAvailabilityReported = true;
                _report(MeetingDiagnosticKind.RefinementAvailability, true, null);
            }
            var candidates = indices.Select(index => turns[index] with
                { StartSeconds = Math.Max(0, turns[index].StartSeconds - origin), EndSeconds = turns[index].EndSeconds - origin }).ToArray();
            var aligned = aligner.AlignAsync(clip.Value.Audio, candidates, _token).GetAwaiter().GetResult();
            if (_disposed) return;
            if (aligned is null || aligned.Count != candidates.Length)
                throw new InvalidDataException("Optional timing refinement returned invalid turns.");
            var clipSeconds = clip.Value.Audio.Samples.Length / (double)clip.Value.Audio.SampleRate;
            var replacements = new MeetingTurn[indices.Length];
            for (var index = 0; index < indices.Length; index++)
            {
                var turn = aligned[index];
                if (turn is null || turn.SpeakerId != candidates[index].SpeakerId || turn.Text != candidates[index].Text ||
                    !double.IsFinite(turn.StartSeconds) || !double.IsFinite(turn.EndSeconds) ||
                    turn.StartSeconds < 0 || turn.EndSeconds < turn.StartSeconds || turn.EndSeconds > clipSeconds)
                    throw new InvalidDataException("Optional timing refinement returned invalid turns.");
                replacements[index] = turns[indices[index]].TimingQuality != MeetingTimingQuality.Aligned &&
                    turn.TimingQuality == MeetingTimingQuality.Aligned
                    ? turn with { StartSeconds = turn.StartSeconds + origin, EndSeconds = turn.EndSeconds + origin }
                    : turns[indices[index]];
            }
            for (var index = 0; index < indices.Length; index++) turns[indices[index]] = replacements[index];
        }
        catch (Exception) when (_disposed) { }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _alignmentUnavailable = true;
            _alignmentAvailabilityReported = true;
            _report(MeetingDiagnosticKind.RefinementAvailability, false, error);
            System.Diagnostics.Trace.TraceWarning("Ansible: optional timing refinement unavailable. {0}", error.Message);
        }
    }

    private void TagSources(MeetingTurn[] turns, IReadOnlyList<MeetingTurn> previous)
    {
        for (var index = 0; index < turns.Length; index++)
        {
            var turn = turns[index];
            if (turn.TimingQuality != MeetingTimingQuality.Aligned) continue;
            var old = index < previous.Count ? previous[index] : null;
            var unchangedBounds = old?.TimingQuality == MeetingTimingQuality.Aligned &&
                old.StartSeconds == turn.StartSeconds && old.EndSeconds == turn.EndSeconds;
            turns[index] = turn with { Source = unchangedBounds ? old!.Source : _timeline.Source(turn.StartSeconds, turn.EndSeconds) };
        }
    }

    public async ValueTask DisposeAsync()
    {
        IMeetingWordAligner? aligner;
        lock (_lifecycle)
        {
            _disposed = true;
            aligner = _aligner;
        }
        ReleaseActivity();
        if (aligner is not null)
        {
            await Task.Run(async () => await aligner.DisposeAsync());
            lock (_lifecycle) _aligner = null;
        }
        if (_observer is { } observer)
        {
            // Failed observer teardown has already released its worker resources; failed aligner teardown may retain native memory for retry.
            try { await observer.DisposeAsync(); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("Ansible: automatic speaker names unavailable. {0}", error.Message);
            }
            _observer = null;
        }
    }

    private void ReleaseActivity()
    {
        if (_attachment is { } attachment) attachment.Source.Activity -= attachment.Activity;
        _attachment = null;
    }
}
