// Compile the production source into this test executable. Test pointer geometry
// runs on its actual input thread via a timer. No SendInput or OS input injection.
#include "record_capture.cpp"
#include "../record-spikes/encoder/record_encoder.h"
#include <iostream>

namespace {
int64_t fixture_started{};
int fixture_stage{};

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
    auto point = [&](LONG x, LONG y, bool start) { add_point(*state, POINT{state->bounds.left + x, state->bounds.top + y}, start); };
    try {
        if (fixture_stage == 0 && elapsed > 0.2) {
            state->drawing = true;
            point(250, 400, true); point(600, 400, false); point(600, 700, false);
            point(250, 700, false); point(250, 400, false);
            state->drawing = false;
            fixture_stage = 1;
        } else if (fixture_stage == 1 && elapsed > 2.0) {
            state->drawing = true;
            point(1100, 550, true); point(1500, 550, false); point(1400, 450, false);
            point(1500, 550, false); point(1400, 650, false);
            state->drawing = false;
            fixture_stage = 2;
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
        GdiplusShutdown(token);
        std::cout << "red-geometry first=" << first_left << "," << first_right << " second=" << second_left << "," << second_right
            << " summary=" << summary_left << "," << summary_right << "\n";
        if (!first_left || first_right || second_left || !second_right || !summary_left || !summary_right) return 1;
        uint32_t frames{}, audio{}; int64_t duration{};
        check_hresult(record_encoder_inspect((output / L"take.mp4").c_str(), &frames, &audio, &duration));
        std::cout << "decoded=" << frames << " audio=" << audio << " duration-ms=" << duration / 10000.0 << "\n";
        std::cout << "FIXTURE PASS. Synthetic geometry exercised real WGC, presentation fences, PNG and silent MP4.\n";
        return frames && !audio ? 0 : 1;
    } catch (...) { std::cerr << "FIXTURE FAILED HRESULT=0x" << std::hex << static_cast<uint32_t>(to_hresult()) << "\n"; return 1; }
}
