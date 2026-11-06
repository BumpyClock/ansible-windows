#include "video_encoder.h"
#include "record_capture.h"
#include <dwmapi.h>
#include <gdiplus.h>
#include <filesystem>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

using namespace Gdiplus;
namespace {
constexpr UINT capture_message = WM_APP + 71;
constexpr uint32_t max_bursts = 256;
constexpr size_t max_points = 65536;
constexpr size_t max_strokes = 4096;
constexpr uint32_t max_burst_pictures = 7;
constexpr int64_t maximum_seconds = 300;

constexpr bool run_limit_reached(int64_t run_started, int64_t now, int64_t frequency) {
    return now - run_started >= maximum_seconds * frequency;
}

struct stroke { std::vector<PointF> points; };
struct burst {
    uint32_t number{};
    int64_t timestamp{};
    std::vector<stroke> strokes;
    bool frame_saved{};
};
struct frame_request { uint32_t number{}; int64_t after_qpc{}; };

struct take {
    HMONITOR monitor{};
    RECT bounds{};
    std::wstring directory;
    int64_t origin{};
    int64_t frequency{};
    std::atomic_bool stop{};
    std::atomic_bool discard{};
    std::atomic_bool abort_media{};
    std::atomic_bool executing{};
    std::atomic_bool ran{};
    std::atomic_bool capture_done{};
    std::atomic<DWORD> input_thread{};
    std::mutex mutex;
    record_take_status status{sizeof(record_take_status)};
    std::vector<burst> bursts;
    std::optional<frame_request> request;
    int64_t summary_after{};
    // Only the input thread accesses the fields below. Shared snapshots use mutex.
    HWND overlay{};
    HWND indicator{};
    HDC ink_dc{};
    HBITMAP ink_bitmap{};
    HGDIOBJ ink_previous{};
    BYTE* ink_pixels{};
    SIZE ink_size{};
    HHOOK mouse_hook{};
    HHOOK keyboard_hook{};
    std::thread capture_worker;
    std::optional<size_t> current;
    std::optional<size_t> shown;
    bool drawing{};
    bool left_monitor{};
    bool dirty{};
    size_t point_count{};
    size_t stroke_count{};
    int64_t last_draw{};
    int64_t last_render{};
    int64_t fade_started{};
    bool summary_requested{};
    bool input_released{};

    std::wstring file(std::wstring const& name) const {
        return (std::filesystem::path(directory) / name).wstring();
    }
    double seconds_since(int64_t timestamp) const {
        return static_cast<double>(qpc_now() - timestamp) / frequency;
    }
    void set_error(int32_t record_take_status::* field, HRESULT error) {
        std::lock_guard guard(mutex);
        if (status.*field == S_OK) status.*field = error;
    }
    void halt(bool discarded) {
        if (discarded) discard.store(true);
        // The first stop owns the real end time. Final picture work can continue.
        { std::lock_guard guard(mutex);
          if (!stop.load()) { status.stop_qpc = qpc_now(); stop.store(true); } }
        if (auto thread = input_thread.load()) PostThreadMessageW(thread, capture_message, 0, 0);
    }
};

std::atomic<take*> active{};

int64_t to_qpc(int64_t time_100ns, int64_t frequency) {
    return time_100ns / 10000000 * frequency + time_100ns % 10000000 * frequency / 10000000;
}

std::wstring clock_label(int64_t timestamp, take const& state) {
    auto seconds = std::max<int64_t>(0, (timestamp - state.origin) / state.frequency);
    auto remainder = seconds % 60;
    return std::to_wstring(seconds / 60) + L":" + (remainder < 10 ? L"0" : L"") + std::to_wstring(remainder);
}

void check_gdi(Status status) {
    if (status != Ok) throw hresult_error(E_FAIL, L"Drawing or PNG encoding failed.");
}

void draw_marks(Graphics& graphics, std::vector<burst> const& bursts, float scale, bool numbers) {
    check_gdi(graphics.SetSmoothingMode(SmoothingModeAntiAlias));
    Pen pen(Color(255, 255, 67, 74), std::max(2.0f, 4.0f * scale));
    pen.SetStartCap(LineCapRound);
    pen.SetEndCap(LineCapRound);
    pen.SetLineJoin(LineJoinRound);
    SolidBrush red(Color(255, 224, 35, 53));
    SolidBrush white(Color(255, 255, 255, 255));
    FontFamily family(L"Segoe UI");
    Font font(&family, std::max(12.0f, 15.0f * scale), FontStyleBold, UnitPixel);
    StringFormat format;
    format.SetAlignment(StringAlignmentCenter);
    format.SetLineAlignment(StringAlignmentCenter);
    for (auto const& b : bursts) {
        for (auto const& s : b.strokes) {
            if (s.points.empty()) continue;
            if (s.points.size() == 1) {
                auto p = s.points.front();
                check_gdi(graphics.DrawEllipse(&pen, p.X * scale - 10 * scale, p.Y * scale - 10 * scale,
                    20 * scale, 20 * scale));
            } else {
                std::vector<PointF> points;
                points.reserve(s.points.size());
                for (auto p : s.points) points.emplace_back(p.X * scale, p.Y * scale);
                check_gdi(graphics.DrawLines(&pen, points.data(), static_cast<INT>(points.size())));
            }
        }
        if (numbers && !b.strokes.empty() && !b.strokes.front().points.empty()) {
            auto p = b.strokes.front().points.front();
            auto radius = std::max(12.0f, 14.0f * scale);
            auto x = std::max(radius, p.X * scale), y = std::max(radius, p.Y * scale);
            RectF label(x - radius, y - radius, radius * 2, radius * 2);
            check_gdi(graphics.FillEllipse(&red, label));
            auto text = std::to_wstring(b.number);
            check_gdi(graphics.DrawString(text.c_str(), -1, &font, label, &format, &white));
        }
    }
}

void draw_frame_label(Graphics& graphics, std::wstring const& label, UINT width) {
    FontFamily family(L"Segoe UI");
    Font font(&family, 20, FontStyleBold, UnitPixel);
    SolidBrush dark(Color(235, 20, 20, 24)), white(Color(255, 255, 255, 255));
    RectF box(12, 12, std::min<float>(width - 24.0f, 660), 38);
    check_gdi(graphics.FillRectangle(&dark, box));
    box.X += 10;
    StringFormat format;
    format.SetLineAlignment(StringAlignmentCenter);
    check_gdi(graphics.DrawString(label.c_str(), -1, &font, box, &format, &white));
}

void save_png(std::wstring const& path, std::vector<BYTE> const& pixels, UINT width, UINT height,
    std::wstring const& label, std::vector<burst> const& marks = {}, float scale = 1) {
    auto picture = pixels;
    Bitmap bitmap(width, height, width * 4, PixelFormat32bppRGB, picture.data());
    check_gdi(bitmap.GetLastStatus());
    Graphics graphics(&bitmap);
    if (!marks.empty()) draw_marks(graphics, marks, scale, true);
    draw_frame_label(graphics, label, width);
    // The built-in GDI+ PNG encoder CLSID. No registry or reflection discovery.
    CLSID png{0x557cf406, 0x1a04, 0x11d3, {0x9a, 0x73, 0x00, 0x00, 0xf8, 0x1e, 0xf3, 0x2e}};
    check_gdi(bitmap.Save(path.c_str(), &png));
}

LRESULT CALLBACK overlay_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if (message == WM_ERASEBKGND) return 1;
    if (message == WM_PAINT) {
        PAINTSTRUCT paint{};
        auto dc = BeginPaint(window, &paint);
        auto state = reinterpret_cast<take*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        RECT area{};
        GetClientRect(window, &area);
        if (state && window == state->indicator) {
            FillRect(dc, &area, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, RGB(255, 255, 255));
            auto font = CreateFontW(-18, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
            auto prior = SelectObject(dc, font);
            DrawTextW(dc, L"● Recording   Esc to discard", -1, &area, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
            SelectObject(dc, prior);
            DeleteObject(font);
        }
        EndPaint(window, &paint);
        return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}

uint32_t elapsed_units(take const& state, int64_t started, int64_t units) {
    return static_cast<uint32_t>((qpc_now() - started) * units / state.frequency);
}

int64_t present(take& state) {
    auto started = qpc_now();
    Bitmap bitmap(state.ink_size.cx, state.ink_size.cy, state.ink_size.cx * 4,
        PixelFormat32bppPARGB, state.ink_pixels);
    check_gdi(bitmap.GetLastStatus());
    {
        Graphics graphics(&bitmap);
        check_gdi(graphics.Clear(Color(0, 0, 0, 0)));
        if (state.shown) {
            std::vector<burst> visible;
            { std::lock_guard guard(state.mutex); visible.push_back(state.bursts[*state.shown]); }
            draw_marks(graphics, visible, 1, false);
        }
        graphics.Flush(FlushIntentionSync);
        check_gdi(graphics.GetLastStatus());
    }
    POINT destination{state.bounds.left, state.bounds.top}, source{};
    BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
    // Publish actual premultiplied pixels. The ink HWND never uses the separate
    // SetLayeredWindowAttributes/WM_PAINT redirection path used by the indicator.
    check_bool(UpdateLayeredWindow(state.overlay, nullptr, &destination, &state.ink_size,
        state.ink_dc, &source, 0, &blend, ULW_ALPHA));
    state.last_render = qpc_now();
    state.dirty = false;
    { std::lock_guard guard(state.mutex);
      state.status.max_present_us = std::max(state.status.max_present_us, elapsed_units(state, started, 1000000)); }
    return state.last_render;
}

// The burst overlay is frozen until WGC acknowledges a frame after this fence.
// New pointer geometry is retained, but only rendered after that acknowledgement.
void finish_burst(take& state) {
    if (!state.current) return;
    { std::lock_guard guard(state.mutex); if (state.request) return; }
    state.shown = state.current;
    auto fence = present(state);
    check_hresult(DwmFlush());
    { std::lock_guard guard(state.mutex);
      state.request = frame_request{state.bursts[*state.current].number, fence}; }
    state.current.reset();
    state.fade_started = 0;
}

void add_point(take& state, POINT point, bool new_stroke) {
    if (!PtInRect(&state.bounds, point)) { state.left_monitor = true; return; }
    new_stroke |= state.left_monitor;
    state.left_monitor = false;
    std::lock_guard guard(state.mutex);
    if (state.point_count >= max_points || (new_stroke && state.stroke_count >= max_strokes))
        throw hresult_error(E_BOUNDS, L"Ink reached the take's geometry limit.");
    if (!state.current) {
        if (state.bursts.size() == max_bursts) throw hresult_error(E_BOUNDS, L"Ink reached the take's burst limit.");
        state.bursts.push_back(burst{static_cast<uint32_t>(state.bursts.size() + 1), qpc_now()});
        state.current = state.bursts.size() - 1;
        state.status.burst_count = static_cast<uint32_t>(state.bursts.size());
        new_stroke = true;
    }
    auto& b = state.bursts[*state.current];
    if (new_stroke || b.strokes.empty()) { b.strokes.emplace_back(); ++state.stroke_count; }
    auto p = PointF(static_cast<float>(point.x - state.bounds.left), static_cast<float>(point.y - state.bounds.top));
    auto& points = b.strokes.back().points;
    if (points.empty() || points.back().X != p.X || points.back().Y != p.Y) {
        points.push_back(p);
        ++state.point_count;
    }
    state.last_draw = qpc_now();
    state.dirty = true;
}

template <typename Forward>
LRESULT handle_mouse_input(take& state, WPARAM message, MSLLHOOKSTRUCT const& mouse, Forward forward) {
    try {
        if (message == WM_LBUTTONDOWN) { state.drawing = true; add_point(state, mouse.pt, true); }
        else if (message == WM_LBUTTONUP) { if (state.drawing) add_point(state, mouse.pt, false); state.drawing = false; }
        else if (message == WM_MOUSEMOVE && state.drawing) add_point(state, mouse.pt, false);
    } catch (...) { state.set_error(&record_take_status::ink_error, to_hresult()); state.halt(false); }
    // Windows must process movement to update the pointer, including while dragging to ink.
    // Consume buttons and wheels globally so clicks and scrolling cannot reach the UI underneath.
    if (message == WM_MOUSEMOVE) return forward();
    return 1;
}

LRESULT CALLBACK mouse_proc(int code, WPARAM message, LPARAM data) {
    auto state = active.load();
    if (code < 0 || !state || state->stop.load()) return CallNextHookEx(nullptr, code, message, data);
    auto const& mouse = *reinterpret_cast<MSLLHOOKSTRUCT const*>(data);
    { std::lock_guard guard(state->mutex);
      auto delay = static_cast<uint32_t>(GetTickCount() - mouse.time);
      ++state->status.mouse_events;
      if (message == WM_MOUSEMOVE) ++state->status.mouse_moves;
      state->status.max_hook_delay_ms = std::max(state->status.max_hook_delay_ms, delay);
      if (delay > 100) ++state->status.hook_delays_over_100ms; }
    return handle_mouse_input(*state, message, mouse,
        [&] { return CallNextHookEx(nullptr, code, message, data); });
}

LRESULT CALLBACK keyboard_proc(int code, WPARAM message, LPARAM data) {
    auto state = active.load();
    if (code >= 0 && state && !state->stop.load()) {
        auto key = reinterpret_cast<KBDLLHOOKSTRUCT const*>(data);
        if (key->vkCode == VK_ESCAPE) {
            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN) state->halt(true);
            return 1;
        }
    }
    return CallNextHookEx(nullptr, code, message, data);
}

// The capture worker owns the matched pixels until this synchronous save returns.
// Release the presentation hold before PNG work so the next burst can be shown.
template <typename Save>
void save_burst_frame(take& state, frame_request const& request, Save save) {
    int64_t timestamp;
    { std::lock_guard guard(state.mutex);
      timestamp = state.bursts[request.number - 1].timestamp;
      state.status.max_fence_wait_ms = std::max(state.status.max_fence_wait_ms,
          elapsed_units(state, request.after_qpc, 1000));
      state.request.reset(); }
    if (auto thread = state.input_thread.load()) PostThreadMessageW(thread, capture_message, 0, 0);
    bool saved{};
    if (request.number <= max_burst_pictures) {
        try { save(request.number, timestamp); saved = true; }
        catch (...) { state.set_error(&record_take_status::frame_error, to_hresult()); }
    }
    { std::lock_guard guard(state.mutex);
      state.bursts[request.number - 1].frame_saved = saved;
      if (saved) ++state.status.picture_count; }
}

void capture_worker(take& state) {
    try {
        apartment_scope apartment;
        mf_scope mf;
        if (!GraphicsCaptureSession::IsSupported()) throw hresult_error(E_NOTIMPL, L"Screen capture is unavailable.");
        GraphicsCaptureItem item{nullptr};
        auto factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        check_hresult(factory->CreateForMonitor(state.monitor, guid_of<GraphicsCaptureItem>(), put_abi(item)));
        auto closed = std::make_shared<std::atomic_bool>(false);
        auto closed_revoker = item.Closed(auto_revoke, [closed](auto const&, auto const&) { closed->store(true); });
        auto size = item.Size();
        if (size.Width <= 0 || size.Height <= 0 || static_cast<int64_t>(size.Width) * size.Height > 33554432)
            throw hresult_error(E_BOUNDS, L"The monitor exceeds the capture allocation limit.");
        double scale = std::min(1.0, 1920.0 / std::max(size.Width, size.Height));
        UINT width = std::max(2u, static_cast<UINT>(size.Width * scale) & ~1u);
        UINT height = std::max(2u, static_cast<UINT>(size.Height * scale) & ~1u);
        { std::lock_guard guard(state.mutex); state.status.width = width; state.status.height = height; }
        com_ptr<ID3D11Device> device;
        com_ptr<ID3D11DeviceContext> context;
        check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, device.put(), nullptr, context.put()));
        auto dxgi = device.as<IDXGIDevice>();
        com_ptr<IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
        auto direct = inspectable.as<IDirect3DDevice>();
        auto pool = Direct3D11CaptureFramePool::CreateFreeThreaded(direct, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
        auto session = pool.CreateCaptureSession(item);
        record_encoder_result evidence{sizeof(record_encoder_result)};
        start_clock(evidence);
        std::unique_ptr<video_encoder> encoder;
        try { encoder = std::make_unique<video_encoder>(state.file(L"take.mp4").c_str(), width, height, evidence); }
        catch (...) { state.set_error(&record_take_status::video_error, to_hresult()); }
        session.StartCapture();
        com_ptr<ID3D11Texture2D> staging;
        std::vector<BYTE> pixels(width * height * 4);
        int64_t latest_qpc{};
        int64_t last_encode_tick{};
        auto started = qpc_now();
        bool video_finished{};
        try {
            for (;;) {
                if (state.discard.load() || state.abort_media.load()) break;
                if (closed->load()) throw hresult_error(RO_E_CLOSED, L"The recorded monitor closed.");
                auto frame = pool.TryGetNextFrame();
                if (frame) {
                    auto content = frame.ContentSize();
                    if (content.Width != size.Width || content.Height != size.Height)
                        throw hresult_error(E_CHANGED_STATE, L"The recorded monitor size changed.");
                    com_ptr<ID3D11Texture2D> texture;
                    auto access = frame.Surface().as<IDirect3DDxgiInterfaceAccess>();
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
                    for (UINT y = 0; y < height; ++y) {
                        auto row = static_cast<BYTE*>(mapped.pData) + static_cast<uint64_t>(y) * size.Height / height * mapped.RowPitch;
                        for (UINT x = 0; x < width; ++x)
                            memcpy(pixels.data() + (y * width + x) * 4, row + static_cast<uint64_t>(x) * size.Width / width * 4, 4);
                    }
                    context->Unmap(staging.get(), 0);
                    auto source_time = frame.SystemRelativeTime().count();
                    latest_qpc = to_qpc(source_time, state.frequency);
                    { std::lock_guard guard(state.mutex); ++state.status.source_frames; }
                    if (encoder && !video_finished && !state.stop.load()) {
                        try {
                            com_ptr<IMFMediaBuffer> buffer;
                            check_hresult(MFCreateMemoryBuffer(width * height * 4, buffer.put()));
                            BYTE* target{};
                            check_hresult(buffer->Lock(&target, nullptr, nullptr));
                            memcpy(target, pixels.data(), pixels.size());
                            check_hresult(buffer->Unlock());
                            check_hresult(buffer->SetCurrentLength(static_cast<DWORD>(pixels.size())));
                            encoder->provide(buffer, source_time, true);
                            if (evidence.frames == 1) {
                                std::lock_guard guard(state.mutex);
                                state.status.first_video_qpc = latest_qpc;
                            }
                        } catch (...) { state.set_error(&record_take_status::video_error, to_hresult()); encoder.reset(); }
                    }
                    frame.Close();
                }
                if (encoder && !video_finished && !state.discard.load() && !state.abort_media.load()) {
                    try {
                        auto tick_clock = qpc_now();
                        if (state.stop.load()) {
                            int64_t stopped;
                            { std::lock_guard guard(state.mutex); stopped = state.status.stop_qpc; }
                            encoder->finish(stopped);
                            video_finished = true;
                            { std::lock_guard guard(state.mutex); state.status.video_finalized = 1; }
                        } else if (!last_encode_tick || tick_clock - last_encode_tick >= state.frequency / 30) {
                            encoder->tick(tick_clock);
                            last_encode_tick = tick_clock;
                        }
                    } catch (...) { state.set_error(&record_take_status::video_error, to_hresult()); encoder.reset(); }
                    { std::lock_guard guard(state.mutex);
                      state.status.encoded_samples = evidence.encoded_samples;
                      state.status.held_samples = evidence.held_samples; }
                }
                if (state.discard.load() || state.abort_media.load()) break;
                std::optional<frame_request> request;
                int64_t summary_after;
                { std::lock_guard guard(state.mutex); request = state.request; summary_after = state.summary_after; }
                if (request && latest_qpc >= request->after_qpc) {
                    save_burst_frame(state, *request, [&](uint32_t number, int64_t timestamp) {
                        save_png(state.file(L"burst-" + std::to_wstring(number) + L".png"), pixels, width, height,
                            std::to_wstring(number) + L" @" + clock_label(timestamp, state));
                    });
                } else if (request && state.seconds_since(request->after_qpc) > 3) {
                    state.set_error(&record_take_status::frame_error, HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                    { std::lock_guard guard(state.mutex); state.request.reset(); }
                    PostThreadMessageW(state.input_thread.load(), capture_message, 0, 0);
                }
                if (summary_after && latest_qpc >= summary_after) {
                    try {
                        std::vector<burst> all;
                        { std::lock_guard guard(state.mutex); all = state.bursts; }
                        auto label = all.empty() ? L"Final @" : L"Summary @";
                        int64_t stopped;
                        { std::lock_guard guard(state.mutex); stopped = state.status.stop_qpc; }
                        save_png(state.file(L"summary.png"), pixels, width, height,
                            std::wstring(label) + clock_label(stopped, state), all, static_cast<float>(scale));
                        { std::lock_guard guard(state.mutex); state.status.summary_saved = 1; ++state.status.picture_count; }
                    } catch (...) { state.set_error(&record_take_status::frame_error, to_hresult()); }
                    break;
                }
                if (summary_after && state.seconds_since(summary_after) > 3) {
                    state.set_error(&record_take_status::frame_error, HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                    break;
                }
                if (!latest_qpc && state.seconds_since(started) > 5)
                    throw hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT), L"Screen capture did not deliver an image.");
                std::this_thread::sleep_for(std::chrono::milliseconds(15));
            }
            session.Close();
            pool.Close();
        } catch (...) { session.Close(); pool.Close(); throw; }
    } catch (...) { state.set_error(&record_take_status::capture_error, to_hresult()); state.halt(false); }
    state.capture_done.store(true);
    PostThreadMessageW(state.input_thread.load(), capture_message, 0, 0);
}

void create_input(take& state) {
    HINSTANCE module{};
    check_bool(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<wchar_t const*>(&overlay_proc), &module));
    WNDCLASSEXW type{sizeof(type)};
    type.lpfnWndProc = overlay_proc;
    type.hInstance = module;
    type.lpszClassName = L"AnsibleRecordInk";
    type.hCursor = LoadCursorW(nullptr, IDC_CROSS);
    if (!RegisterClassExW(&type) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) throw_last_error();
    // Start in the topmost group. A later HWND_TOPMOST promotion reported success
    // but left this inactive HWND ordinary in the foreground-app regression.
    state.overlay = CreateWindowExW(WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        type.lpszClassName, L"Ansible ink", WS_POPUP, state.bounds.left, state.bounds.top,
        state.bounds.right - state.bounds.left, state.bounds.bottom - state.bounds.top,
        nullptr, nullptr, module, nullptr);
    check_pointer(state.overlay);
    SetWindowLongPtrW(state.overlay, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(&state));
    state.ink_size = SIZE{state.bounds.right - state.bounds.left, state.bounds.bottom - state.bounds.top};
    if (state.ink_size.cx <= 0 || state.ink_size.cy <= 0 ||
        static_cast<int64_t>(state.ink_size.cx) * state.ink_size.cy > 33554432)
        throw hresult_error(E_BOUNDS, L"The monitor exceeds the ink allocation limit.");
    state.ink_dc = CreateCompatibleDC(nullptr);
    check_pointer(state.ink_dc);
    BITMAPINFO info{};
    info.bmiHeader = {sizeof(BITMAPINFOHEADER), state.ink_size.cx, -state.ink_size.cy, 1, 32, BI_RGB};
    void* pixels{};
    state.ink_bitmap = CreateDIBSection(state.ink_dc, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
    check_pointer(state.ink_bitmap);
    state.ink_pixels = static_cast<BYTE*>(pixels);
    state.ink_previous = SelectObject(state.ink_dc, state.ink_bitmap);
    check_pointer(state.ink_previous);
    present(state);
    check_bool(SetWindowPos(state.overlay, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW));
    state.indicator = CreateWindowExW(WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        type.lpszClassName, L"Ansible recording", WS_POPUP, state.bounds.left + 16, state.bounds.top + 16,
        280, 38, nullptr, nullptr, module, nullptr);
    check_pointer(state.indicator);
    SetWindowLongPtrW(state.indicator, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(&state));
    check_bool(SetLayeredWindowAttributes(state.indicator, 0, 240, LWA_ALPHA));
    // An indicator privacy failure aborts the media take; do not record it anyway.
    check_bool(SetWindowDisplayAffinity(state.indicator, WDA_EXCLUDEFROMCAPTURE));
    check_bool(SetWindowPos(state.indicator, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW));
    UpdateWindow(state.indicator);
    state.mouse_hook = SetWindowsHookExW(WH_MOUSE_LL, mouse_proc, module, 0);
    check_pointer(state.mouse_hook);
    state.keyboard_hook = SetWindowsHookExW(WH_KEYBOARD_LL, keyboard_proc, module, 0);
    check_pointer(state.keyboard_hook);
}

template <typename Unhook, typename LastError>
void release_hook(take& state, HHOOK& hook, Unhook unhook, LastError last_error) noexcept {
    if (!hook) return;
    if (unhook(hook)) { hook = nullptr; return; }
    auto error = last_error();
    // A timed-out hook can already have been removed by Windows.
    if (error == ERROR_INVALID_HOOK_HANDLE) hook = nullptr;
    else state.set_error(&record_take_status::ink_error, HRESULT_FROM_WIN32(error));
}

void release_hooks(take& state) noexcept {
    release_hook(state, state.mouse_hook, UnhookWindowsHookEx, GetLastError);
    release_hook(state, state.keyboard_hook, UnhookWindowsHookEx, GetLastError);
}

void release_input(take& state) noexcept {
    auto failed = [&state] { state.set_error(&record_take_status::ink_error, HRESULT_FROM_WIN32(GetLastError())); };
    release_hooks(state);
    if (state.overlay) {
        if (DestroyWindow(state.overlay)) state.overlay = nullptr;
        else failed();
    }
    if (state.indicator) {
        if (DestroyWindow(state.indicator)) state.indicator = nullptr;
        else failed();
    }
    if (state.ink_previous) {
        if (SelectObject(state.ink_dc, state.ink_previous)) state.ink_previous = nullptr;
        else failed();
    }
    if (state.ink_bitmap && !state.ink_previous) {
        if (DeleteObject(state.ink_bitmap)) { state.ink_bitmap = nullptr; state.ink_pixels = nullptr; }
        else failed();
    }
    if (state.ink_dc && !state.ink_previous) {
        if (DeleteDC(state.ink_dc)) state.ink_dc = nullptr;
        else failed();
    }
}
}

extern "C" int32_t __cdecl record_take_create(void* monitor, wchar_t const* directory, int64_t origin_qpc, void** result) {
    if (!monitor || !directory || !result || origin_qpc <= 0) return E_INVALIDARG;
    *result = nullptr;
    try {
        auto state = std::make_unique<take>();
        state->monitor = static_cast<HMONITOR>(monitor);
        MONITORINFO info{sizeof(info)};
        check_bool(GetMonitorInfoW(state->monitor, &info));
        state->bounds = info.rcMonitor;
        state->directory = directory;
        if (!std::filesystem::is_directory(state->directory)) return HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
        LARGE_INTEGER frequency{};
        check_bool(QueryPerformanceFrequency(&frequency));
        state->frequency = frequency.QuadPart;
        state->origin = origin_qpc;
        if (origin_qpc > qpc_now()) return E_INVALIDARG;
        *result = state.release();
        return S_OK;
    } catch (...) { return to_hresult(); }
}

extern "C" int32_t __cdecl record_take_run(void* instance) {
    if (!instance) return E_INVALIDARG;
    auto& state = *static_cast<take*>(instance);
    if (state.ran.exchange(true)) return E_ILLEGAL_METHOD_CALL;
    take* expected{};
    if (!active.compare_exchange_strong(expected, &state)) return HRESULT_FROM_WIN32(ERROR_BUSY);
    // The managed operation owns the take deadline and cancels both speech and media.
    // This standalone-native safety limit must not expire earlier because admission was delayed.
    auto run_started = qpc_now();
    state.executing.store(true);
    { std::lock_guard guard(state.mutex); state.status.running = 1; }
    HRESULT outcome = S_OK;
    ULONG_PTR gdi_token{};
    GdiplusStartupInput gdi_input;
    auto old_dpi = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    try {
        check_gdi(GdiplusStartup(&gdi_token, &gdi_input, nullptr));
        state.input_thread.store(GetCurrentThreadId());
        // Establish the message queue before another thread can signal it.
        MSG message{};
        PeekMessageW(&message, nullptr, 0, 0, PM_NOREMOVE);
        if (!state.stop.load()) {
            create_input(state);
            state.capture_worker = std::thread([&state] { capture_worker(state); });
            while (!state.capture_done.load()) {
                auto iteration_started = qpc_now();
                while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
                    if (message.message == WM_QUIT) state.halt(false);
                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                }
                if (!state.stop.load() && run_limit_reached(run_started, qpc_now(), state.frequency)) state.halt(false);
                if (state.discard.load()) break;
                bool pending;
                { std::lock_guard guard(state.mutex); pending = state.request.has_value(); }
                if (state.stop.load()) {
                    // Input is released immediately. Media snapshots still own the frozen ink window.
                    if (!state.input_released) {
                        auto style = GetWindowLongPtrW(state.overlay, GWL_EXSTYLE);
                        check_bool(SetWindowLongPtrW(state.overlay, GWL_EXSTYLE, style | WS_EX_TRANSPARENT) != 0);
                        check_bool(SetWindowLongPtrW(state.indicator, GWL_EXSTYLE,
                            GetWindowLongPtrW(state.indicator, GWL_EXSTYLE) | WS_EX_TRANSPARENT) != 0);
                        state.input_released = true;
                    }
                    release_hooks(state);
                    state.drawing = false;
                    if (!pending && state.current) { finish_burst(state); pending = true; }
                    if (!pending && !state.summary_requested) {
                        state.shown.reset();
                        auto fence = present(state);
                        check_hresult(DwmFlush());
                        { std::lock_guard guard(state.mutex); state.summary_after = fence; }
                        state.summary_requested = true;
                    }
                } else if (!pending) {
                    if (state.current) {
                        state.shown = state.current;
                        state.fade_started = 0;
                        if (state.dirty && state.seconds_since(state.last_render) >= 1.0 / 60) present(state);
                        if (!state.drawing && state.seconds_since(state.last_draw) >= 1.5) finish_burst(state);
                    } else if (state.shown) {
                        if (!state.fade_started) state.fade_started = qpc_now();
                        auto remaining = 1.0 - state.seconds_since(state.fade_started);
                        if (remaining <= 0) { state.shown.reset(); present(state); state.fade_started = 0; }
                        else {
                            BLENDFUNCTION blend{AC_SRC_OVER, 0, static_cast<BYTE>(255 * remaining), AC_SRC_ALPHA};
                            POINT source{};
                            check_bool(UpdateLayeredWindow(state.overlay, nullptr, nullptr, nullptr,
                                state.ink_dc, &source, 0, &blend, ULW_ALPHA));
                        }
                    }
                }
                { std::lock_guard guard(state.mutex);
                  state.status.max_loop_us = std::max(state.status.max_loop_us,
                      elapsed_units(state, iteration_started, 1000000)); }
                MsgWaitForMultipleObjects(0, nullptr, FALSE, 16, QS_ALLINPUT);
            }
        }
    } catch (...) { outcome = to_hresult(); state.set_error(&record_take_status::ink_error, outcome); state.halt(false); }
    // A discarded/failed pump must release input before any blocking media teardown.
    state.halt(state.discard.load());
    if (state.discard.load() || FAILED(outcome)) {
        // A failed input pump cannot provide the final presentation fence.
        state.abort_media.store(true);
    }
    release_input(state);
    { std::lock_guard guard(state.mutex);
      state.status.hooks_released_before_join = !state.mouse_hook && !state.keyboard_hook ? 1u : 0u; }
    if (state.capture_worker.joinable()) state.capture_worker.join();
    active.store(nullptr);
    state.input_thread.store(0);
    if (gdi_token) GdiplusShutdown(gdi_token);
    if (old_dpi) SetThreadDpiAwarenessContext(old_dpi);
    { std::lock_guard guard(state.mutex); state.status.running = 0; state.status.discarded = state.discard.load() ? 1 : 0; }
    state.executing.store(false);
    return outcome;
}

extern "C" int32_t __cdecl record_take_stop(void* instance, int32_t discard) {
    if (!instance || (discard != 0 && discard != 1)) return E_INVALIDARG;
    try { static_cast<take*>(instance)->halt(discard != 0); return S_OK; }
    catch (...) { return to_hresult(); }
}

extern "C" int32_t __cdecl record_take_get_status(void* instance, record_take_status* result) {
    if (!instance || !result || result->size != sizeof(*result)) return E_INVALIDARG;
    auto& state = *static_cast<take*>(instance);
    std::lock_guard guard(state.mutex);
    *result = state.status;
    result->discarded = state.discard.load() ? 1 : 0;
    return S_OK;
}

extern "C" int32_t __cdecl record_take_get_burst(void* instance, uint32_t index, record_take_burst* result) {
    if (!instance || !result || result->size != sizeof(*result)) return E_INVALIDARG;
    auto& state = *static_cast<take*>(instance);
    std::lock_guard guard(state.mutex);
    if (index >= state.bursts.size()) return E_BOUNDS;
    auto const& b = state.bursts[index];
    *result = record_take_burst{sizeof(*result), b.number, b.frame_saved ? 1u : 0u, 0, b.timestamp};
    return S_OK;
}

extern "C" int32_t __cdecl record_take_destroy(void* instance) {
    if (!instance) return E_INVALIDARG;
    auto state = static_cast<take*>(instance);
    if (state->executing.load()) return E_PENDING;
    // Do not free an object whose hooks or windows failed to release. Managed ownership blocks admission.
    if (state->mouse_hook || state->keyboard_hook || state->overlay || state->indicator ||
        state->ink_dc || state->ink_bitmap || state->ink_previous) return E_PENDING;
    delete state;
    return S_OK;
}
