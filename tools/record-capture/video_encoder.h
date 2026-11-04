#pragma once
#define NOMINMAX
#include "../record-spikes/encoder/record_encoder.h"
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <memory>
#include <thread>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
using ::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess;

struct mf_scope {
    mf_scope() { check_hresult(MFStartup(MF_VERSION)); }
    ~mf_scope() { MFShutdown(); }
};
struct apartment_scope {
    apartment_scope() { init_apartment(apartment_type::multi_threaded); }
    ~apartment_scope() { uninit_apartment(); }
};

static com_ptr<IMFMediaType> video_type(GUID subtype, UINT width, UINT height) {
    com_ptr<IMFMediaType> type;
    check_hresult(MFCreateMediaType(type.put()));
    check_hresult(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    check_hresult(type->SetGUID(MF_MT_SUBTYPE, subtype));
    check_hresult(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    check_hresult(MFSetAttributeSize(type.get(), MF_MT_FRAME_SIZE, width, height));
    check_hresult(MFSetAttributeRatio(type.get(), MF_MT_FRAME_RATE, 30, 1));
    check_hresult(MFSetAttributeRatio(type.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    return type;
}

static int64_t qpc_now() {
    LARGE_INTEGER value{};
    check_bool(QueryPerformanceCounter(&value));
    return value.QuadPart;
}

static int64_t qpc_100ns(int64_t value, int64_t frequency) {
    return value / frequency * 10000000 + value % frequency * 10000000 / frequency;
}

static void start_clock(record_encoder_result& result) {
    LARGE_INTEGER frequency{};
    check_bool(QueryPerformanceFrequency(&frequency));
    result.start_qpc = qpc_now();
    result.qpc_frequency = frequency.QuadPart;
}

// Owns one immutable latest image and one pending sample. Quiet input still
// advances on the real capture clock; held images are counted separately.
class video_encoder {
    com_ptr<IMFSinkWriter> writer;
    DWORD stream{};
    com_ptr<IMFMediaBuffer> latest;
    com_ptr<IMFSample> pending;
    int64_t pending_time{};
    uint64_t generation{};
    uint64_t prior_generation{};
    bool pending_held{};
    record_encoder_result& result;

    void write_pending(int64_t duration) {
        if (duration <= 0) throw hresult_error(E_UNEXPECTED, L"Encoder clock did not advance.");
        check_hresult(pending->SetSampleDuration(duration));
        check_hresult(writer->WriteSample(stream, pending.get()));
        auto submitted = qpc_now();
        if (result.encoded_samples == 0) result.first_encode_qpc = submitted;
        result.last_encode_qpc = submitted;
        ++result.encoded_samples;
        if (pending_held) ++result.held_samples;
    }

public:
    video_encoder(const wchar_t* output, UINT width, UINT height, record_encoder_result& evidence) : result(evidence) {
        com_ptr<IMFAttributes> attributes;
        check_hresult(MFCreateAttributes(attributes.put(), 1));
        check_hresult(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE));
        check_hresult(MFCreateSinkWriterFromURL(output, nullptr, attributes.get(), writer.put()));
        auto encoded = video_type(MFVideoFormat_H264, width, height);
        check_hresult(encoded->SetUINT32(MF_MT_AVG_BITRATE, 6000000));
        check_hresult(writer->AddStream(encoded.get(), &stream));
        auto raw = video_type(MFVideoFormat_RGB32, width, height);
        check_hresult(raw->SetUINT32(MF_MT_DEFAULT_STRIDE, width * 4));
        check_hresult(writer->SetInputMediaType(stream, raw.get(), nullptr));
        check_hresult(writer->BeginWriting());
    }

    void provide(com_ptr<IMFMediaBuffer> const& image, int64_t source_time, bool wgc_update) {
        latest = image;
        if (generation == 0) result.first_frame_100ns = source_time;
        result.last_frame_100ns = source_time;
        ++generation;
        if (wgc_update) ++result.frames;
    }

    void tick(int64_t sampled_qpc = 0) {
        if (!latest) return;
        auto time = result.encoded_samples == 0 && !pending ? 0 :
            qpc_100ns(sampled_qpc ? sampled_qpc : qpc_now(), result.qpc_frequency) - result.first_frame_100ns;
        if (pending) {
            if (time <= pending_time) return;
            write_pending(time - pending_time);
        }
        pending = nullptr;
        check_hresult(MFCreateSample(pending.put()));
        check_hresult(pending->AddBuffer(latest.get()));
        check_hresult(pending->SetSampleTime(time));
        pending_time = time;
        pending_held = generation == prior_generation;
        prior_generation = generation;
    }

    void finish(int64_t stopped) {
        result.stop_qpc = stopped;
        if (!pending) throw hresult_error(E_FAIL, L"No source image reached the encoder.");
        auto end = qpc_100ns(stopped, result.qpc_frequency) - result.first_frame_100ns;
        write_pending(end - pending_time);
        pending = nullptr;
        check_hresult(writer->Finalize());
    }
};

