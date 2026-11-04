#define NOMINMAX
#include "record_encoder.h"
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

    void tick() {
        if (!latest) return;
        auto time = result.encoded_samples == 0 && !pending ? 0 :
            qpc_100ns(qpc_now(), result.qpc_frequency) - result.first_frame_100ns;
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

extern "C" int32_t __cdecl record_encoder_run(const wchar_t* output, uint32_t seconds,
    int32_t request_borderless, void* cancellation_event, record_encoder_result* result) {
    if (!output || !result || result->size != sizeof(*result) || seconds < 1 || seconds > 300)
        return E_INVALIDARG;
    *result = record_encoder_result{sizeof(*result)};
    try {
        apartment_scope apartment;
        mf_scope mf;
        if (!GraphicsCaptureSession::IsSupported()) return E_NOTIMPL;
        start_clock(*result);

        POINT pointer{};
        check_bool(GetCursorPos(&pointer));
        auto monitor = MonitorFromPoint(pointer, MONITOR_DEFAULTTONEAREST);
        auto factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        GraphicsCaptureItem item{nullptr};
        check_hresult(factory->CreateForMonitor(monitor, guid_of<GraphicsCaptureItem>(), put_abi(item)));
        auto source_closed = std::make_shared<std::atomic_bool>(false);
        auto closed_revoker = item.Closed(auto_revoke, [source_closed](auto const&, auto const&) {
            source_closed->store(true, std::memory_order_relaxed);
        });
        auto size = item.Size();
        // The cap is a spike parameter, not the product's encoder choice.
        double scale = std::min(1.0, 1920.0 / std::max(size.Width, size.Height));
        UINT width = static_cast<UINT>(size.Width * scale) & ~1u;
        UINT height = static_cast<UINT>(size.Height * scale) & ~1u;
        result->width = width;
        result->height = height;

        com_ptr<ID3D11Device> device;
        com_ptr<ID3D11DeviceContext> context;
        check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
            device.put(), nullptr, context.put()));
        auto dxgi = device.as<IDXGIDevice>();
        com_ptr<IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
        auto direct = inspectable.as<IDirect3DDevice>();
        auto pool = Direct3D11CaptureFramePool::CreateFreeThreaded(direct,
            DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
        auto session = pool.CreateCaptureSession(item);
        result->borderless_hresult = S_FALSE;
        if (request_borderless) {
            try {
                // No consent prompt in this spike. A missing capability is evidence.
                session.IsBorderRequired(false);
                result->borderless_hresult = S_OK;
            } catch (...) { result->borderless_hresult = to_hresult(); }
        }
        result->border_required = session.IsBorderRequired() ? 1 : 0;

        video_encoder encoder(output, width, height, *result);
        session.StartCapture();

        com_ptr<ID3D11Texture2D> staging;
        auto started = std::chrono::steady_clock::now();
        auto next = started;
        try {
            while (std::chrono::steady_clock::now() - started < std::chrono::seconds(seconds)) {
                if (cancellation_event && WaitForSingleObject(cancellation_event, 0) == WAIT_OBJECT_0) break;
                if (source_closed->load(std::memory_order_relaxed))
                    throw hresult_error(RO_E_CLOSED, L"The captured monitor closed.");
                auto frame = pool.TryGetNextFrame();
                if (frame) {
                    auto frame_size = frame.ContentSize();
                    if (frame_size.Width != size.Width || frame_size.Height != size.Height)
                        throw hresult_error(E_CHANGED_STATE, L"Monitor size changed during spike.");
                    auto access = frame.Surface().as<IDirect3DDxgiInterfaceAccess>();
                    com_ptr<ID3D11Texture2D> texture;
                    check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), texture.put_void()));
                    if (!staging) {
                        D3D11_TEXTURE2D_DESC desc{};
                        texture->GetDesc(&desc);
                        desc.Usage = D3D11_USAGE_STAGING;
                        desc.BindFlags = 0;
                        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                        desc.MiscFlags = 0;
                        check_hresult(device->CreateTexture2D(&desc, nullptr, staging.put()));
                    }
                    context->CopyResource(staging.get(), texture.get());
                    D3D11_MAPPED_SUBRESOURCE mapped{};
                    check_hresult(context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped));
                    com_ptr<IMFMediaBuffer> buffer;
                    BYTE* bytes{};
                    try {
                        check_hresult(MFCreateMemoryBuffer(width * height * 4, buffer.put()));
                        check_hresult(buffer->Lock(&bytes, nullptr, nullptr));
                        for (UINT y = 0; y < height; ++y) {
                            auto source = static_cast<BYTE*>(mapped.pData) +
                                (static_cast<uint64_t>(y) * size.Height / height) * mapped.RowPitch;
                            auto destination = bytes + y * width * 4;
                            for (UINT x = 0; x < width; ++x)
                                memcpy(destination + x * 4, source +
                                    (static_cast<uint64_t>(x) * size.Width / width) * 4, 4);
                        }
                        check_hresult(buffer->Unlock());
                        bytes = nullptr;
                        check_hresult(buffer->SetCurrentLength(width * height * 4));
                    } catch (...) {
                        if (bytes) buffer->Unlock();
                        context->Unmap(staging.get(), 0);
                        throw;
                    }
                    context->Unmap(staging.get(), 0);
                    encoder.provide(buffer, frame.SystemRelativeTime().count(), true);
                    frame.Close();
                }
                encoder.tick();
                next += std::chrono::microseconds(33333);
                std::this_thread::sleep_until(next);
                // Never accumulate a catch-up queue if the encoder falls behind.
                if (next < std::chrono::steady_clock::now()) next = std::chrono::steady_clock::now();
            }
            auto stopped = qpc_now();
            session.Close();
            pool.Close();
            encoder.finish(stopped);
        } catch (...) {
            session.Close();
            pool.Close();
            throw;
        }
        return S_OK;
    } catch (...) { return to_hresult(); }
}

extern "C" int32_t __cdecl record_encoder_static_fixture(const wchar_t* output, uint32_t seconds,
    record_encoder_result* result) {
    if (!output || !result || result->size != sizeof(*result) || seconds < 1 || seconds > 300)
        return E_INVALIDARG;
    *result = record_encoder_result{sizeof(*result)};
    try {
        apartment_scope apartment;
        mf_scope mf;
        start_clock(*result);
        constexpr UINT width = 640, height = 360;
        result->width = width;
        result->height = height;
        video_encoder encoder(output, width, height, *result);
        com_ptr<IMFMediaBuffer> buffer;
        check_hresult(MFCreateMemoryBuffer(width * height * 4, buffer.put()));
        BYTE* pixels{};
        check_hresult(buffer->Lock(&pixels, nullptr, nullptr));
        // A single synthetic image: red top, blue bottom. No WGC source updates.
        for (UINT y = 0; y < height; ++y)
            for (UINT x = 0; x < width; ++x) {
                auto p = pixels + (y * width + x) * 4;
                p[0] = y < height / 2 ? 32 : 220;
                p[1] = 32;
                p[2] = y < height / 2 ? 220 : 32;
                p[3] = 255;
            }
        const char* label = "SYNTHETIC STATIC FIXTURE";
        auto glyph = [](char c) -> const char* {
            switch (c) {
                case 'S': return "111100111001111"; case 'Y': return "101101010010010";
                case 'N': return "101111111111101"; case 'T': return "111010010010010";
                case 'H': return "101101111101101"; case 'E': return "111100110100111";
                case 'I': return "111010010010111"; case 'C': return "111100100100111";
                case 'A': return "010101111101101"; case 'F': return "111100110100100";
                case 'X': return "101101010101101"; case 'U': return "101101101101111";
                case 'R': return "110101110101101"; default: return "000000000000000";
            }
        };
        constexpr UINT scale = 6;
        for (UINT c = 0; label[c]; ++c)
            for (UINT y = 0; y < 5 * scale; ++y)
                for (UINT x = 0; x < 3 * scale; ++x)
                    if (glyph(label[c])[y / scale * 3 + x / scale] == '1') {
                        auto p = pixels + ((32 + y) * width + 32 + c * 4 * scale + x) * 4;
                        p[0] = p[1] = p[2] = 255;
                    }
        check_hresult(buffer->Unlock());
        check_hresult(buffer->SetCurrentLength(width * height * 4));
        encoder.provide(buffer, qpc_100ns(qpc_now(), result->qpc_frequency), false);
        auto started = std::chrono::steady_clock::now();
        auto next = started;
        while (std::chrono::steady_clock::now() - started < std::chrono::seconds(seconds)) {
            encoder.tick();
            next += std::chrono::microseconds(33333);
            std::this_thread::sleep_until(next);
            if (next < std::chrono::steady_clock::now()) next = std::chrono::steady_clock::now();
        }
        encoder.finish(qpc_now());
        return S_OK;
    } catch (...) { return to_hresult(); }
}

extern "C" int32_t __cdecl record_encoder_inspect(const wchar_t* input, uint32_t* video_frames,
    uint32_t* audio_streams, int64_t* duration_100ns) {
    if (!input || !video_frames || !audio_streams || !duration_100ns) return E_INVALIDARG;
    try {
        apartment_scope apartment;
        mf_scope mf;
        com_ptr<IMFSourceReader> reader;
        com_ptr<IMFAttributes> attributes;
        check_hresult(MFCreateAttributes(attributes.put(), 1));
        check_hresult(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE));
        check_hresult(MFCreateSourceReaderFromURL(input, attributes.get(), reader.put()));
        *audio_streams = 0;
        for (DWORD i = 0; ; ++i) {
            com_ptr<IMFMediaType> type;
            auto hr = reader->GetNativeMediaType(i, 0, type.put());
            if (hr == MF_E_INVALIDSTREAMNUMBER) break;
            check_hresult(hr);
            GUID major{};
            check_hresult(type->GetGUID(MF_MT_MAJOR_TYPE, &major));
            if (major == MFMediaType_Audio) ++*audio_streams;
            if (major == MFMediaType_Video) {
                GUID subtype{};
                check_hresult(type->GetGUID(MF_MT_SUBTYPE, &subtype));
                if (subtype != MFVideoFormat_H264) return MF_E_INVALIDMEDIATYPE;
            }
        }
        PROPVARIANT duration{};
        check_hresult(reader->GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE,
            MF_PD_DURATION, &duration));
        *duration_100ns = duration.uhVal.QuadPart;
        PropVariantClear(&duration);
        com_ptr<IMFMediaType> decoded;
        check_hresult(MFCreateMediaType(decoded.put()));
        check_hresult(decoded->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        check_hresult(decoded->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32));
        check_hresult(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, decoded.get()));
        *video_frames = 0;
        for (;;) {
            DWORD flags{};
            LONGLONG timestamp{};
            com_ptr<IMFSample> sample;
            check_hresult(reader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                nullptr, &flags, &timestamp, sample.put()));
            if (sample) ++*video_frames;
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
        }
        return S_OK;
    } catch (...) { return to_hresult(); }
}
