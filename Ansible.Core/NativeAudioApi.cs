using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ansible.Core;

internal static partial class NativeAudioApi
{
    private const string Library = "audiocpp";
    private static readonly NativeModule Module = new(new NativeModuleLoader());

    public static void Initialize(string path) => Module.Initialize(path);

    public static void Check(int status, string operation)
    {
        if (status != 0)
        {
            var detail = Marshal.PtrToStringUTF8(LastError()) ?? $"native status {status}";
            throw new InvalidOperationException($"audio.cpp could not {operation}: {detail}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModelConfig
    {
        public nint Family;
        public nint ConfigId;
        public nint WeightId;
        public nint SpecOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BackendConfig
    {
        public nint Backend;
        public int Device;
        public int Threads;
    }

    [LibraryImport(Library, EntryPoint = "audiocpp_build_version")]
    internal static partial nint BuildVersion();
    [LibraryImport(Library, EntryPoint = "audiocpp_last_error")]
    private static partial nint LastError();
    [LibraryImport(Library, EntryPoint = "audiocpp_registry_create")]
    internal static partial int RegistryCreate(nint config, out nint registry);
    [LibraryImport(Library, EntryPoint = "audiocpp_registry_free")]
    internal static partial void RegistryFree(nint registry);
    [LibraryImport(Library, EntryPoint = "audiocpp_registry_family_count")]
    internal static partial nuint FamilyCount(nint registry);
    [LibraryImport(Library, EntryPoint = "audiocpp_registry_family")]
    internal static partial int Family(nint registry, nuint index, out nint family);
    [LibraryImport(Library, EntryPoint = "audiocpp_model_load", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ModelLoad(nint registry, string path, in ModelConfig config, nint options, out nint model);
    [LibraryImport(Library, EntryPoint = "audiocpp_model_free")]
    internal static partial void ModelFree(nint model);
    [LibraryImport(Library, EntryPoint = "audiocpp_model_supports", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Supports(nint model, string task, string mode);
    [LibraryImport(Library, EntryPoint = "audiocpp_model_option_count")]
    internal static partial nuint OptionCount(nint model, int scope);
    [LibraryImport(Library, EntryPoint = "audiocpp_model_option")]
    internal static partial int Option(nint model, int scope, nuint index, out nint name,
        nint valueName, nint description, nint defaultValue, nint minimum, nint maximum, nint required);
    [LibraryImport(Library, EntryPoint = "audiocpp_session_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SessionCreate(nint model, string task, string mode,
        in BackendConfig backend, nint options, out nint session);
    [LibraryImport(Library, EntryPoint = "audiocpp_session_free")]
    internal static partial void SessionFree(nint session);
    [LibraryImport(Library, EntryPoint = "audiocpp_session_run")]
    internal static partial int SessionRun(nint session, nint request, out nint result);
    [LibraryImport(Library, EntryPoint = "audiocpp_request_create")]
    internal static partial nint RequestCreate();
    [LibraryImport(Library, EntryPoint = "audiocpp_request_free")]
    internal static partial void RequestFree(nint request);
    [LibraryImport(Library, EntryPoint = "audiocpp_request_set_text", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SetText(nint request, string text, string? language);
    [LibraryImport(Library, EntryPoint = "audiocpp_request_set_text_language", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SetTextLanguage(nint request, string language);
    [LibraryImport(Library, EntryPoint = "audiocpp_request_set_option", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SetOption(nint request, string key, string value);
    [LibraryImport(Library, EntryPoint = "audiocpp_request_set_audio")]
    internal static unsafe partial int SetAudio(nint request, float* samples, nuint frames, int sampleRate, int channels);
    [LibraryImport(Library, EntryPoint = "audiocpp_result_text")]
    internal static partial int ResultText(nint result, out nint text, out nint language);
    [LibraryImport(Library, EntryPoint = "audiocpp_result_segment_count")]
    internal static partial nuint SegmentCount(nint result);
    [LibraryImport(Library, EntryPoint = "audiocpp_result_segment")]
    internal static partial int Segment(nint result, nuint index, nint start, nint end, nint confidence, out nint text);
    [LibraryImport(Library, EntryPoint = "audiocpp_result_speaker_turn_count")]
    internal static partial nuint SpeakerTurnCount(nint result);
    [LibraryImport(Library, EntryPoint = "audiocpp_result_speaker_turn")]
    internal static partial int SpeakerTurn(nint result, nuint index, nint start, nint end,
        nint speakerId, nint confidence, out nint text);
    [LibraryImport(Library, EntryPoint = "audiocpp_result_free")]
    internal static partial void ResultFree(nint result);
    [LibraryImport(Library, EntryPoint = "audiocpp_stream_policy")]
    internal static partial int StreamPolicy(nint session, nint input, nint output, out long preferredFrames, out double preferredSeconds);
    [LibraryImport(Library, EntryPoint = "audiocpp_stream_start")]
    internal static partial int StreamStart(nint session, nint request);
    [LibraryImport(Library, EntryPoint = "audiocpp_stream_push")]
    internal static unsafe partial int StreamPush(nint session, float* samples, nuint frames,
        int sampleRate, int channels, long startSample, out nint streamEvent);
    [LibraryImport(Library, EntryPoint = "audiocpp_stream_next_event")]
    internal static partial int StreamNextEvent(nint session, out nint streamEvent);
    [LibraryImport(Library, EntryPoint = "audiocpp_stream_finish")]
    internal static partial int StreamFinish(nint session, out nint result);
    [LibraryImport(Library, EntryPoint = "audiocpp_stream_reset")]
    internal static partial int StreamReset(nint session);
    [LibraryImport(Library, EntryPoint = "audiocpp_event_as_result")]
    internal static partial nint EventResult(nint streamEvent);
    [LibraryImport(Library, EntryPoint = "audiocpp_event_free")]
    internal static partial void EventFree(nint streamEvent);
}

internal enum NativeHandleKind { Registry, Model, Session, Request, Result, Event }

internal sealed class NativeAudioHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly NativeHandleKind _kind;

    public NativeAudioHandle(nint handle, NativeHandleKind kind) : base(true)
    {
        _kind = kind;
        SetHandle(handle);
        if (IsInvalid)
        {
            throw new InvalidOperationException($"audio.cpp returned an invalid {kind} handle.");
        }
    }

    protected override bool ReleaseHandle()
    {
        switch (_kind)
        {
            case NativeHandleKind.Registry: NativeAudioApi.RegistryFree(handle); break;
            case NativeHandleKind.Model: NativeAudioApi.ModelFree(handle); break;
            case NativeHandleKind.Session: NativeAudioApi.SessionFree(handle); break;
            case NativeHandleKind.Request: NativeAudioApi.RequestFree(handle); break;
            case NativeHandleKind.Result: NativeAudioApi.ResultFree(handle); break;
            case NativeHandleKind.Event: NativeAudioApi.EventFree(handle); break;
        }
        return true;
    }
}
