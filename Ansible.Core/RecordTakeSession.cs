using System.Diagnostics;
using System.Text;

namespace Ansible.Core;

public enum ShortcutActivation { Voice, Record }

/// <summary>Prepared delivery text. Speech remains separately authoritative for usage and recognition.</summary>
public sealed record BundlePayload(string Text, IReadOnlyList<RecordFrame> Frames, string? VideoPath = null)
{
    public bool IsRecordTake { get; init; }
}
public sealed record RecordFrame(string Path, int? BurstNumber, TimeSpan Time, bool IsSummary = false);
public sealed record RecordInkBurst(int Number, long Timestamp);
/// <summary>OriginTimestamp and every media timestamp use Stopwatch's QPC clock.</summary>
public sealed record RecordTakeOptions(nint Monitor, long OriginTimestamp, bool IncludeVideoLink);
public sealed record RecordMediaResult(IReadOnlyList<RecordFrame> Frames, string? VideoPath,
    IReadOnlyList<string> Warnings, bool Discarded = false, TimeSpan? VideoStartOffset = null);

public interface IRecordTakeFactory
{
    RecordTakeOptions Snapshot(bool includeVideoLink);
    Task<IRecordTakeCapture> StartAsync(RecordTakeOptions options, Action<RecordInkBurst> onBurst,
        Action onDiscard, CancellationToken cancellationToken);
}

public interface IRecordTakeCapture : IAsyncDisposable
{
    bool IsReleased { get; }
    Task<RecordMediaResult> StopAsync(bool discard);
}

/// <summary>One take owns media startup, replacement previews, burst anchors and final bundle preparation.</summary>
public sealed class RecordTakeSession
{
    private readonly object _gate = new();
    private readonly RecordTakeOptions _options;
    private readonly AudioModel _model;
    private readonly Task<IRecordTakeCapture> _starting;
    private readonly List<(RecordInkBurst Burst, int? PreviewLength)> _bursts = [];
    private readonly List<(long Timestamp, int Length)> _previews = [];
    private readonly List<string> _warnings = [];
    private TranscriptUpdate? _preview;
    private IRecordTakeCapture? _capture;
    private Task<RecordMediaResult>? _stopping;
    private long? _audioOrigin;
    private bool _discard;

    public RecordTakeSession(IRecordTakeFactory factory, RecordTakeOptions options, AudioModel model,
        Action onDiscard, CancellationToken cancellationToken)
    {
        _options = options;
        _model = model;
        _previews.Add((options.OriginTimestamp, 0));
        // Factory work never runs on the input or UI thread, including implementations that initialize synchronously.
        _starting = Task.Run(() => factory.StartAsync(options, OnBurst, () =>
        {
            lock (_gate) _discard = true;
            onDiscard();
        }, cancellationToken));
    }

    public bool HasRetainedResources => _capture?.IsReleased == false;
    public string? TimingNotice => _model.Id == AudioModelDefaults.RecognitionModelId ||
        _model.Family is "kroko_asr" or "vibevoice_asr_streaming" ? null :
        "This model has no live word timing. Ink times will appear in an Ink timeline after the transcript.";
    public IReadOnlyList<string> Warnings { get { lock (_gate) return _warnings.ToArray(); } }

    public void SetAudioOrigin(long? timestamp) { lock (_gate) _audioOrigin = timestamp; }
    public void Update(TranscriptUpdate update)
    {
        lock (_gate)
        {
            _preview = update;
            _previews.Add((Stopwatch.GetTimestamp(), update.SpeechText?.Length ?? 0));
            if (_previews.Count > 8192) _previews.RemoveAt(0);
        }
    }

    private void OnBurst(RecordInkBurst burst)
    {
        lock (_gate)
        {
            // The native boundary bounds bursts for the five-minute take. Reject invalid external metadata.
            if (burst.Number <= 0 || burst.Timestamp < _options.OriginTimestamp ||
                _bursts.Count >= 2048 || _bursts.Any(item => item.Burst.Number == burst.Number))
                throw new InvalidDataException("The capture returned an invalid ink timeline.");
            var preview = _previews.FindLastIndex(value => value.Timestamp <= burst.Timestamp);
            _bursts.Add((burst, preview >= 0 ? _previews[preview].Length : null));
        }
    }

    public Task<RecordMediaResult> StopAsync(bool discard)
    {
        lock (_gate)
        {
            var escalate = discard && !_discard;
            _discard |= discard;
            if (_stopping is null) _stopping = StopCoreAsync();
            else if (escalate) _stopping = DiscardStoppedAsync(_stopping);
            return _stopping;
        }
    }

    private async Task<RecordMediaResult> DiscardStoppedAsync(Task<RecordMediaResult> stopped)
    {
        var media = await stopped.ConfigureAwait(false);
        if (_capture is not null)
        {
            try
            {
                // Recognition can fail after Finish has already released capture. Retract that take's files too.
                await _capture.StopAsync(true).ConfigureAwait(false);
                await _capture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (_gate) _warnings.Add($"Discarded capture could not be fully released. {error.Message}");
            }
        }
        return media with { Discarded = true };
    }

    private async Task<RecordMediaResult> StopCoreAsync()
    {
        RecordMediaResult media;
        try
        {
            _capture = await _starting.ConfigureAwait(false);
            bool discard;
            lock (_gate) discard = _discard;
            media = await _capture.StopAsync(discard).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            media = new([], null, [$"Screen capture failed. {error.Message}"]);
        }
        if (_capture is not null)
        {
            try { await _capture.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (_gate) _warnings.Add($"Screen capture could not release all resources. {error.Message}");
            }
        }
        lock (_gate)
        {
            _warnings.AddRange(media.Warnings);
            return media with { Discarded = media.Discarded || _discard };
        }
    }

    public async Task ReleaseRetainedAsync()
    {
        if (_capture is not null) await _capture.DisposeAsync().ConfigureAwait(false);
        if (HasRetainedResources) throw new IOException("Screen capture resources remain owned.");
    }

    public async Task<BundlePayload> PrepareAsync(RecognitionResult result)
    {
        var speech = result.SpeechText ?? throw new InvalidOperationException("Authoritative speech is unavailable.");
        if (string.IsNullOrWhiteSpace(speech)) throw new InvalidOperationException("No speech was recognized.");
        var media = await StopAsync(false).ConfigureAwait(false);
        if (media.Discarded) throw new OperationCanceledException("The record take was discarded.");
        (RecordInkBurst Burst, int? PreviewLength)[] bursts;
        long? audioOrigin;
        lock (_gate) { bursts = _bursts.OrderBy(item => item.Burst.Timestamp).ToArray(); audioOrigin = _audioOrigin; }
        var insertions = new List<(int Offset, string Marker)>();
        var timeline = new List<string>();
        var native = _model.Id == AudioModelDefaults.RecognitionModelId && audioOrigin is not null &&
            result.WordTimings is { Words.Count: > 0 };
        var approximate = _model.Preview == "live" && _model.Id != AudioModelDefaults.RecognitionModelId;
        // Catalog preview values are explicit; the current live models also identify their native families.
        approximate |= _model.Family is "kroko_asr" or "vibevoice_asr_streaming";
        foreach (var item in bursts)
        {
            var time = Stopwatch.GetElapsedTime(_options.OriginTimestamp, item.Burst.Timestamp);
            // An @ timestamp becomes a file mention in composers such as T3 Code.
            var label = $"ink {item.Burst.Number} at {FormatTime(time)}";
            if (native)
            {
                var sample = (item.Burst.Timestamp - audioOrigin!.Value) * (16000d / Stopwatch.Frequency);
                var word = result.WordTimings!.Words.FirstOrDefault(value => value.StartSample >= sample);
                insertions.Add((word?.TextStart ?? speech.Length, $"[{label}]"));
            }
            else if (approximate && item.PreviewLength is { } length)
            {
                var offset = Math.Min(length, speech.Length);
                while (offset < speech.Length && offset > 0 && !char.IsWhiteSpace(speech[offset - 1])) offset++;
                insertions.Add((offset, $"[{label}, approximate]"));
            }
            else timeline.Add($"[{label}]");
        }
        var text = new StringBuilder(speech);
        foreach (var insertion in insertions.Select((value, order) => (value.Offset, value.Marker, order))
            .OrderByDescending(value => value.Offset).ThenByDescending(value => value.order))
        {
            var before = insertion.Offset > 0 && !char.IsWhiteSpace(speech[insertion.Offset - 1]) ? " " : "";
            var after = insertion.Offset < speech.Length && !char.IsWhiteSpace(speech[insertion.Offset]) ? " " : "";
            text.Insert(insertion.Offset, before + insertion.Marker + after);
        }
        if (timeline.Count > 0)
        {
            text.Append("\nInk timeline\n").AppendJoin('\n', timeline);
            lock (_gate) _warnings.Add("This model has no usable word timing. Ink times are listed in the trailing timeline.");
        }
        if (media.Frames.Count > 0)
        {
            text.Append("\nFrames in order: ");
            text.AppendJoin(", ", media.Frames.Select(frame => frame.IsSummary ? "summary (all ink)" :
                frame.BurstNumber is { } number ? $"{number} at {FormatTime(frame.Time)}" : "final screen"));
            text.Append('.');
        }
        if (_options.IncludeVideoLink && media.VideoPath is { } path)
        {
            text.Append("\nVideo: ").Append(new Uri(Path.GetFullPath(path)).AbsoluteUri);
            if (media.VideoStartOffset is { } offset && offset > TimeSpan.Zero)
                text.Append(" (video begins at ").Append(offset.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" s on the take clock)");
        }
        return new(text.ToString(), media.Frames, media.VideoPath) { IsRecordTake = true };
    }

    private static string FormatTime(TimeSpan time) =>
        $"{(int)time.TotalMinutes}:{time.Seconds:00}";
}
