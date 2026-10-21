using System.Diagnostics;
using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Probe;

internal static class StreamingProbe
{
    internal static async Task<(RecognitionResult Transcript, StreamMeasurement Measurement)> MeasureAsync(
        IRecognitionEngine engine, AudioModel model, byte[] pcm, RecognitionOptions options,
        Func<TimeSpan>? elapsed = null, Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var audio = Channel.CreateBounded<byte[]>(8);
        var progressGate = new object();
        TimeSpan? endOfInput = null;
        TimeSpan? firstSpeechPreview = null;
        var previewsBeforeEndOfInput = 0;
        var sourceDuration = TimeSpan.FromSeconds(pcm.Length / 32000.0);
        // These two seams let tests advance pacing deterministically; production uses a monotonic Stopwatch.
        var clock = Stopwatch.StartNew();
        elapsed ??= () => clock.Elapsed;
        delayAsync ??= Task.Delay;
        var streaming = engine.StreamAsync(model, audio.Reader, options,
            new InlineProgress<TranscriptUpdate>(update =>
            {
                if (update.IsFinal || string.IsNullOrWhiteSpace(update.SpeechText))
                    return;
                lock (progressGate)
                {
                    firstSpeechPreview ??= elapsed();
                    if (endOfInput is null)
                        previewsBeforeEndOfInput++;
                }
            }), timeout.Token);
        Exception? primaryFailure = null;
        Task? producerStep = null;
        try
        {
            for (var offset = 0; offset < pcm.Length; offset += 1280)
            {
                // A packet is available only after its last sample would have been captured.
                // Absolute deadlines avoid accumulating inference backpressure in later packet pacing.
                var count = Math.Min(1280, pcm.Length - offset);
                var scheduled = TimeSpan.FromSeconds((offset + count) / 32000.0);
                var remaining = scheduled - elapsed();
                while (remaining > TimeSpan.Zero)
                {
                    producerStep = delayAsync(
                        TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), timeout.Token);
                    await AwaitInputStepAsync(producerStep, streaming);
                    remaining = scheduled - elapsed();
                }
                producerStep = audio.Writer.WriteAsync(
                    pcm.AsSpan(offset, count).ToArray(), timeout.Token).AsTask();
                await AwaitInputStepAsync(producerStep, streaming);
            }
            lock (progressGate)
                endOfInput = elapsed();
            audio.Writer.Complete();
            var liveTranscript = await streaming;
            var finalElapsed = elapsed();
            if (string.IsNullOrWhiteSpace(liveTranscript.SpeechText))
            {
                throw new InvalidDataException("The native live stream returned no authoritative speech.");
            }
            lock (progressGate)
                return (liveTranscript, new StreamMeasurement(firstSpeechPreview, previewsBeforeEndOfInput,
                    endOfInput.Value, finalElapsed - endOfInput.Value, finalElapsed - sourceDuration, finalElapsed));
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            audio.Writer.TryComplete();
            timeout.Cancel();
            if (producerStep is not null)
            {
                try { await producerStep; }
                catch when (primaryFailure is not null) { }
            }
            try { await streaming; }
            catch (Exception cleanupError) when (primaryFailure is not null)
            {
                if (!ReferenceEquals(primaryFailure, cleanupError) && cleanupError is not OperationCanceledException)
                    throw new AggregateException("Probe input and native recognition cleanup failed.", primaryFailure, cleanupError);
            }
        }
    }

    private static async Task AwaitInputStepAsync(Task inputStep, Task<RecognitionResult> streaming)
    {
        await Task.WhenAny(inputStep, streaming);
        if (streaming.IsCompleted)
        {
            await streaming;
            throw new InvalidDataException("The native engine ended the live stream before audio input finished.");
        }
        await inputStep;
        if (streaming.IsCompleted)
        {
            await streaming;
            throw new InvalidDataException("The native engine ended the live stream before audio input finished.");
        }
    }

    internal static string FormatSummary(string name, IEnumerable<double> samples, string unit)
    {
        var sorted = samples.Order().ToArray();
        if (sorted.Length == 0)
            return $"{name}: none (n=0)";
        var median = sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
            : sorted[sorted.Length / 2];
        return $"{name}: median={median:F1}, min={sorted[0]:F1}, max={sorted[^1]:F1} {unit} (n={sorted.Length})";
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

internal sealed record StreamMeasurement(TimeSpan? FirstSpeechPreview, int PreviewsBeforeEndOfInput,
    TimeSpan EndOfInput, TimeSpan FinalResultLatency, TimeSpan SourceDurationToFinal, TimeSpan FullElapsed);
