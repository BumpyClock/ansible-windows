using System.Runtime.InteropServices;

namespace Ansible.Core;

public sealed partial class NativeAudioEngine
{
    internal Task PrepareAlignmentAsync(AudioModel model, CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => NativeOperation.Run(() =>
        {
            RequireAlignmentModel(model);
            EnsureSession(model, "offline", 16000L * 30 * sizeof(float), new RecognitionOptions(Backend: NativeBackend.Cpu), cancellationToken);
            return true;
        }, success => { if (!success) ReleaseModel(); }), cancellationToken);

    internal Task<IReadOnlyList<MeetingTimedToken>> ReadAlignmentTokensAsync(AudioModel model, WaveAudio audio,
        CancellationToken cancellationToken) => RunExclusiveAsync<IReadOnlyList<MeetingTimedToken>>(() => NativeOperation.Run(() =>
        {
            RequireAlignmentModel(model);
            var bytes = NativeMemory.ValidateAudio(audio, cancellationToken);
            if (audio.SampleRate != 16000 || audio.Channels != 1 || audio.Samples.Length > 16000 * 30)
                throw new InvalidDataException("Timestamp alignment accepts at most 30 seconds of 16 kHz mono audio.");
            var options = new RecognitionOptions(Backend: NativeBackend.Cpu);
            EnsureSession(model, "offline", bytes, options, cancellationToken);
            using var request = CreateRequest(options, cancellationToken);
            NativeOperation.Step(cancellationToken, () => SetRequestAudio(request, audio));
            NativeAudioApi.Check(NativeAudioApi.SessionRun(_session!.DangerousGetHandle(), request.DangerousGetHandle(), out var pointer),
                "recognize optional timestamp anchors on CPU");
            using var result = new NativeAudioHandle(pointer, NativeHandleKind.Result);
            cancellationToken.ThrowIfCancellationRequested();
            var count = NativeAudioApi.WordCount(pointer);
            if (count > 8192) throw new InvalidDataException("Timestamp alignment returned too many token anchors.");
            var words = new List<MeetingTimedToken>((int)count);
            for (nuint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NativeAudioApi.Check(NativeAudioApi.Word(pointer, index, out var text, out var start, out var end, 0),
                    "read recognized timestamp anchors");
                words.Add(new MeetingTimedToken(Marshal.PtrToStringUTF8(text)
                    ?? throw new InvalidDataException("A timestamp anchor has no text."), start, end));
            }
            return (IReadOnlyList<MeetingTimedToken>)words;
        }, success => { if (!success) CleanupOperation(false); }), cancellationToken);

    private static void RequireAlignmentModel(AudioModel model)
    {
        if (model.Family != "nemotron_asr")
            throw new NotSupportedException("Optional meeting timestamp alignment requires Nemotron ASR on CPU.");
    }
}
