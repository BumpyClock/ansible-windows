// Compile the production source into this test executable. Test pointer geometry
// runs on its actual input thread via a timer. No SendInput or OS input injection.
#include "record_capture.cpp"
#include "../record-spikes/encoder/record_encoder.h"
#include <iostream>

namespace {
int64_t fixture_started{};
int fixture_stage{};

void save_visible_drag(take& state) {
    // Read the composed desktop while the production input handler still owns
    // the drag. A saved summary alone cannot prove that live ink was visible.
    auto width = state.bounds.right - state.bounds.left;
    auto height = state.bounds.bottom - state.bounds.top;
    std::vector<BYTE> pixels(static_cast<size_t>(width) * height * 4);
    struct desktop_image {
        HDC screen{GetDC(nullptr)};
        HDC memory{};
        HBITMAP bitmap{};
        HGDIOBJ previous{};
        ~desktop_image() {
            if (previous) SelectObject(memory, previous);
            if (bitmap) DeleteObject(bitmap);
            if (memory) DeleteDC(memory);
            if (screen) ReleaseDC(nullptr, screen);
        }
    } image;
    check_pointer(image.screen);
    image.memory = CreateCompatibleDC(image.screen);
    check_pointer(image.memory);
    image.bitmap = CreateCompatibleBitmap(image.screen, width, height);
    check_pointer(image.bitmap);
    image.previous = SelectObject(image.memory, image.bitmap);
    check_pointer(image.previous);
    check_bool(BitBlt(image.memory, 0, 0, width, height, image.screen,
        state.bounds.left, state.bounds.top, SRCCOPY | CAPTUREBLT));
    SelectObject(image.memory, image.previous);
    image.previous = nullptr;
    BITMAPINFO info{};
    info.bmiHeader = {sizeof(BITMAPINFOHEADER), width, -height, 1, 32, BI_RGB};
    check_bool(GetDIBits(image.memory, image.bitmap, 0, height, pixels.data(), &info, DIB_RGB_COLORS) == height);
    if (!state.drawing) throw hresult_error(E_FAIL, L"Fixture released its drag before inspecting live ink.");
    save_png(state.file(L"during-drag.png"), pixels, width, height, L"Synthetic drag is still held");
}

LRESULT CALLBACK fixture_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if (message == WM_PAINT) {
        PAINTSTRUCT paint{};
        auto dc = BeginPaint(window, &paint);
        RECT area{};
        GetClientRect(window, &area);
        auto brush = CreateSolidBrush(RGB(238, 241, 246));
        FillRect(dc, &area, brush);
        DeleteObject(brush);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(35, 50, 75));
        auto font = CreateFontW(-30, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        auto prior = SelectObject(dc, font);
        RECT text{80, 100, area.right - 40, 180};
        DrawTextW(dc, L"SYNTHETIC INK FIXTURE. No OS input injected.", -1, &text, DT_LEFT | DT_SINGLELINE);
        text.top = 180; text.bottom = 240;
        DrawTextW(dc, L"Burst 1. Left rectangle.   Burst 2. Right arrow.", -1, &text, DT_LEFT | DT_SINGLELINE);
        SelectObject(dc, prior);
        DeleteObject(font);
        EndPaint(window, &paint);
        return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}

VOID CALLBACK fixture_timer(HWND, UINT, UINT_PTR, DWORD) {
    auto state = active.load();
    if (!state || !fixture_started) return;
    auto elapsed = state->seconds_since(fixture_started);
    auto send = [&](WPARAM message, LONG x, LONG y) {
        MSLLHOOKSTRUCT input{};
        input.pt = POINT{state->bounds.left + x, state->bounds.top + y};
        handle_mouse_input(*state, message, input, []() -> LRESULT { return 0; });
    };
    try {
        if (fixture_stage == 0 && elapsed > 0.2) {
            send(WM_LBUTTONDOWN, 250, 400);
            send(WM_MOUSEMOVE, 600, 400); send(WM_MOUSEMOVE, 600, 700);
            send(WM_MOUSEMOVE, 250, 700); send(WM_MOUSEMOVE, 250, 400);
            fixture_stage = 1;
        } else if (fixture_stage == 1 && elapsed > 0.8) {
            save_visible_drag(*state);
            fixture_stage = 2;
        } else if (fixture_stage == 2 && elapsed > 0.9) {
            send(WM_LBUTTONUP, 250, 400);
            fixture_stage = 3;
        } else if (fixture_stage == 3 && elapsed > 2.6) {
            send(WM_LBUTTONDOWN, 1100, 550);
            send(WM_MOUSEMOVE, 1500, 550); send(WM_MOUSEMOVE, 1400, 450);
            send(WM_MOUSEMOVE, 1500, 550); send(WM_LBUTTONUP, 1400, 650);
            fixture_stage = 4;
        } else if (elapsed > 4.2) state->halt(false);
    } catch (...) { state->set_error(&record_take_status::ink_error, to_hresult()); state->halt(false); }
}

bool red_at(std::wstring const& path, UINT x, UINT y) {
    Bitmap image(path.c_str());
    check_gdi(image.GetLastStatus());
    // Monitor coordinates scale down uniformly, so both test geometry and image
    // inspection use the reported scale. Check a neighborhood for anti-aliasing.
    for (UINT yy = y - 4; yy <= y + 4; ++yy)
        for (UINT xx = x - 4; xx <= x + 4; ++xx) {
            Color color;
            check_gdi(image.GetPixel(xx, yy, &color));
            if (color.GetR() > 180 && color.GetG() < 110 && color.GetB() < 130) return true;
        }
    return false;
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc == 2 && std::wstring(argv[1]) == L"--input-self-check") {
        take state;
        state.bounds = RECT{0, 0, 1000, 1000};
        LARGE_INTEGER frequency{};
        check_bool(QueryPerformanceFrequency(&frequency));
        state.frequency = frequency.QuadPart;
        state.origin = qpc_now();
        unsigned forwarded{};
        auto forward = [&]() -> LRESULT { ++forwarded; return 12345; };
        auto send = [&](WPARAM message, LONG x, LONG y) {
            MSLLHOOKSTRUCT input{};
            input.pt = POINT{x, y};
            return handle_mouse_input(state, message, input, forward);
        };
        if (send(WM_MOUSEMOVE, 10, 10) != 12345 || forwarded != 1 || !state.bursts.empty()) return 1;
        if (send(WM_LBUTTONDOWN, 20, 30) != 1 || forwarded != 1 || !state.drawing) return 1;
        if (send(WM_MOUSEMOVE, 40, 50) != 12345 || forwarded != 2) return 1;
        if (state.bursts.size() != 1 || state.bursts[0].strokes[0].points.size() != 2) return 1;
        if (send(WM_LBUTTONUP, 60, 70) != 1 || forwarded != 2 || state.drawing) return 1;
        auto const& points = state.bursts[0].strokes[0].points;
        if (points.size() != 3 || points[1].X != 40 || points[1].Y != 50 || points[2].X != 60) return 1;
        for (auto message : {WM_RBUTTONDOWN, WM_RBUTTONUP, WM_MBUTTONDOWN, WM_MBUTTONUP,
                WM_XBUTTONDOWN, WM_XBUTTONUP, WM_MOUSEWHEEL, WM_MOUSEHWHEEL})
            if (send(message, 60, 70) != 1 || forwarded != 2) return 1;
        if (send(WM_MOUSEMOVE, 1200, 1200) != 12345 || forwarded != 3) return 1;
        std::cout << "INPUT SELF-CHECK PASS. Movement forwards while hover and drag ink stay distinct; buttons and wheels are consumed. No OS input injected.\n";
        return 0;
    }
    if (argc != 2) return 2;
    try {
        auto output = std::filesystem::absolute(argv[1]);
        std::filesystem::create_directories(output);
        POINT pointer{};
        check_bool(GetCursorPos(&pointer));
        auto monitor = MonitorFromPoint(pointer, MONITOR_DEFAULTTONEAREST);
        MONITORINFO info{sizeof(info)};
        check_bool(GetMonitorInfoW(monitor, &info));
        auto w = info.rcMonitor.right - info.rcMonitor.left;
        auto h = info.rcMonitor.bottom - info.rcMonitor.top;
        if (w < 1600 || h < 800) throw hresult_error(E_BOUNDS, L"Fixture requires a 1600x800 monitor.");
        auto module = GetModuleHandleW(nullptr);
        WNDCLASSW type{};
        type.lpfnWndProc = fixture_proc; type.hInstance = module; type.lpszClassName = L"AnsibleInkSyntheticFixture";
        check_bool(RegisterClassW(&type));
        auto old_dpi = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        auto background = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, type.lpszClassName, L"Synthetic ink fixture",
            WS_POPUP, info.rcMonitor.left, info.rcMonitor.top, w, h, nullptr, nullptr, module, nullptr);
        check_pointer(background);
        check_bool(SetWindowPos(background, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW));
        UpdateWindow(background);
        fixture_started = qpc_now();
        void* instance{};
        check_hresult(record_take_create(monitor, output.c_str(), fixture_started, &instance));
        auto timer = SetTimer(nullptr, 0, 20, fixture_timer);
        check_bool(timer != 0);
        auto hr = record_take_run(instance);
        KillTimer(nullptr, timer);
        DestroyWindow(background);
        if (old_dpi) SetThreadDpiAwarenessContext(old_dpi);
        record_take_status state{sizeof(state)};
        check_hresult(record_take_get_status(instance, &state));
        check_hresult(record_take_destroy(instance));
        std::cout << "run=0x" << std::hex << static_cast<uint32_t>(hr)
            << " capture=0x" << static_cast<uint32_t>(state.capture_error) << " ink=0x" << static_cast<uint32_t>(state.ink_error)
            << " video=0x" << static_cast<uint32_t>(state.video_error) << " frame=0x" << static_cast<uint32_t>(state.frame_error)
            << std::dec << " bursts=" << state.burst_count << " pictures=" << state.picture_count
            << " summary=" << state.summary_saved << " encoded=" << state.encoded_samples << " held=" << state.held_samples << "\n";
        check_hresult(hr); check_hresult(state.capture_error); check_hresult(state.ink_error);
        check_hresult(state.video_error); check_hresult(state.frame_error);
        if (state.burst_count != 2 || state.picture_count != 3 || !state.summary_saved || state.discarded) return 1;
        ULONG_PTR token{};
        GdiplusStartupInput input;
        check_gdi(GdiplusStartup(&token, &input, nullptr));
        auto scale = static_cast<double>(state.width) / w;
        auto inspect = [&](wchar_t const* file, UINT x, UINT y) { return red_at((output / file).wstring(), static_cast<UINT>(x * scale), static_cast<UINT>(y * scale)); };
        bool first_left = inspect(L"burst-1.png", 450, 400), first_right = inspect(L"burst-1.png", 1250, 550);
        bool second_left = inspect(L"burst-2.png", 450, 400), second_right = inspect(L"burst-2.png", 1250, 550);
        bool summary_left = inspect(L"summary.png", 450, 400), summary_right = inspect(L"summary.png", 1250, 550);
        bool drag_left = red_at((output / L"during-drag.png").wstring(), 450, 400);
        bool drag_right = red_at((output / L"during-drag.png").wstring(), 1250, 550);
        GdiplusShutdown(token);
        std::cout << "red-geometry first=" << first_left << "," << first_right << " second=" << second_left << "," << second_right
            << " summary=" << summary_left << "," << summary_right
            << " held-drag=" << drag_left << "," << drag_right << "\n";
        if (!first_left || first_right || second_left || !second_right || !summary_left || !summary_right || !drag_left || drag_right) return 1;
        uint32_t frames{}, audio{}; int64_t duration{};
        check_hresult(record_encoder_inspect((output / L"take.mp4").c_str(), &frames, &audio, &duration));
        std::cout << "decoded=" << frames << " audio=" << audio << " duration-ms=" << duration / 10000.0 << "\n";
        std::cout << "FIXTURE PASS. Synthetic geometry exercised real WGC, presentation fences, PNG and silent MP4.\n";
        return frames && !audio ? 0 : 1;
    } catch (...) { std::cerr << "FIXTURE FAILED HRESULT=0x" << std::hex << static_cast<uint32_t>(to_hresult()) << "\n"; return 1; }
}
