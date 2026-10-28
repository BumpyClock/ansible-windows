using System.Diagnostics;

namespace Ansible.Core;

internal sealed class NativeDiagnosticContext(LocalDiagnosticLog log, long generation, Guid operationId,
    NativeBackend requested, bool backgroundPreload = false)
{
    public NativeBackend? ActualBackend { get; private set; }
    public bool? SessionReused { get; private set; }
    public NativeDiagnosticSource? SelectionSource { get; set; }

    public void Write(NativeDiagnosticStage stage, AudioModel model, long started,
        NativeBackend? actual = null, bool? reused = null, Exception? error = null)
    {
        if (stage == NativeDiagnosticStage.Session && error is null)
        {
            ActualBackend = actual;
            SessionReused = reused;
        }
        var admission = error as NativeMemoryAdmissionException;
        log.Write(new NativeDiagnostic(operationId, DateTimeOffset.UtcNow, stage,
            error is OperationCanceledException ? DiagnosticOutcome.Cancelled :
            error is null ? DiagnosticOutcome.Completed : DiagnosticOutcome.Failed,
            requested, actual, SafeFamily(model.Family),
            stage == NativeDiagnosticStage.Selection ? SelectionSource : null,
            stage == NativeDiagnosticStage.Fallback ? null : Stopwatch.GetElapsedTime(started).TotalMilliseconds, reused,
            (error as NativeExecutionException)?.StatusCode, error?.HResult)
        {
            RequiredMemoryBytes = admission?.RequiredBytes,
            AvailablePhysicalBytes = admission?.AvailablePhysicalBytes,
            AvailableCommitBytes = admission?.AvailableCommitBytes,
            BackgroundPreload = backgroundPreload
        }, generation);
    }

    public static string? SafeModelId(string id) => id switch
    {
        "moonshine-tiny" or "vibevoice-streaming-1.5b-q4" or "vibevoice-asr-7b-q8" or
        "qwen3-asr-0.6b-q8" or "nemotron-asr-0.6b-q8" or "kroko-en-64l-q8" or
        "nemotron-3-diarization-q8" => id,
        _ => null
    };

    public static DiagnosticModelFamily? SafeFamily(string? family) => family switch
    {
        "moonshine_asr" => DiagnosticModelFamily.Moonshine,
        "vibevoice_asr_streaming" => DiagnosticModelFamily.VibeVoiceStreaming,
        "vibevoice_asr" => DiagnosticModelFamily.VibeVoice,
        "qwen3_asr" => DiagnosticModelFamily.Qwen,
        "nemotron_asr" => DiagnosticModelFamily.Nemotron,
        "kroko_asr" => DiagnosticModelFamily.Kroko,
        "nemotron_3_diar" => DiagnosticModelFamily.NemotronDiarization,
        _ => null
    };
}
