#define NOMINMAX
#include "record_capture.h"
#include "../record-spikes/encoder/record_encoder.h"
#include <windows.h>
#include <filesystem>
#include <iostream>
#include <thread>
#include <chrono>

static void require(int32_t hr, const char* stage) {
    if (hr < 0) { std::cerr << stage << " HRESULT=0x" << std::hex << static_cast<uint32_t>(hr) << "\n"; std::exit(1); }
}

int wmain(int argc, wchar_t** argv) {
    bool capture = argc == 4 && std::wstring(argv[1]) == L"--capture";
    bool check = argc == 3 && std::wstring(argv[1]) == L"--self-check";
    if (!capture && !check) {
        std::cerr << "Usage: record_capture_host --self-check <output-directory>\n"
            "       record_capture_host --capture <output-directory> <seconds1..10>\n"
            "Capture blocks buttons and scrolling on every monitor until stop; the pointer can move. Esc discards. No keys or clicks are injected.\n";
        return 2;
    }
    auto output = std::filesystem::absolute(argv[2]);
    std::filesystem::create_directories(output);
    POINT point{};
    if (!GetCursorPos(&point)) return 1;
    auto monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
    LARGE_INTEGER origin{}, frequency{};
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&origin);
    void* take{};
    if (record_take_create(nullptr, output.c_str(), origin.QuadPart, &take) != E_INVALIDARG) return 1;
    require(record_take_create(monitor, output.c_str(), origin.QuadPart, &take), "create");
    if (check) {
        record_take_status wrong{};
        if (record_take_get_status(take, &wrong) != E_INVALIDARG) return 1;
        require(record_take_stop(take, 1), "pre-run stop");
        require(record_take_run(take), "pre-stopped run");
        record_take_status state{sizeof(state)};
        require(record_take_get_status(take, &state), "status");
        if (state.running || !state.discarded || state.burst_count || state.picture_count || !state.stop_qpc) return 1;
        if (record_take_run(take) != E_ILLEGAL_METHOD_CALL) return 1;
        require(record_take_destroy(take), "destroy");
        std::cout << "SELF-CHECK PASS status-size=" << sizeof(state) << " burst-size=" << sizeof(record_take_burst)
            << " pre-run discard preserved, repeated run rejected, no input overlay opened\n";
        return 0;
    }
    auto seconds = _wtoi(argv[3]);
    if (seconds < 1 || seconds > 10) return 2;
    std::thread timer([take, seconds] {
        std::this_thread::sleep_for(std::chrono::seconds(seconds));
        record_take_stop(take, 0);
    });
    auto hr = record_take_run(take);
    timer.join();
    record_take_status state{sizeof(state)};
    require(record_take_get_status(take, &state), "status");
    std::cout << "run=0x" << std::hex << static_cast<uint32_t>(hr)
        << " capture=0x" << static_cast<uint32_t>(state.capture_error)
        << " ink=0x" << static_cast<uint32_t>(state.ink_error)
        << " video=0x" << static_cast<uint32_t>(state.video_error)
        << " pictures=0x" << static_cast<uint32_t>(state.frame_error) << std::dec
        << " discarded=" << state.discarded << " bursts=" << state.burst_count
        << " saved=" << state.picture_count << " summary=" << state.summary_saved
        << " size=" << state.width << "x" << state.height
        << " source-frames=" << state.source_frames << " encoded=" << state.encoded_samples << " held=" << state.held_samples
        << " first-video-qpc=" << state.first_video_qpc << " stop-qpc=" << state.stop_qpc
        << " video-start-offset-ms=" << (state.first_video_qpc ? (state.first_video_qpc - origin.QuadPart) * 1000.0 / frequency.QuadPart : -1)
        << "\n";
    require(record_take_destroy(take), "destroy");
    std::cout << "hook-events=" << state.mouse_events << " moves=" << state.mouse_moves
        << " max-hook-delay-ms=" << state.max_hook_delay_ms << " over-100ms=" << state.hook_delays_over_100ms
        << " max-present-us=" << state.max_present_us << " max-loop-us=" << state.max_loop_us
        << " max-fence-wait-ms=" << state.max_fence_wait_ms
        << " hooks-released-before-join=" << state.hooks_released_before_join << "\n";
    if (state.discarded) return 3;
    if (hr < 0 || state.capture_error < 0 || state.ink_error < 0 || state.video_error < 0 || state.frame_error < 0 || !state.summary_saved || !state.video_finalized) return 1;
    uint32_t frames{}, audio{};
    int64_t duration{};
    require(record_encoder_inspect((output / L"take.mp4").c_str(), &frames, &audio, &duration), "independent video decode");
    std::cout << "decoded-frames=" << frames << " audio-streams=" << audio << " duration-ms=" << duration / 10000.0 << "\n";
    return frames && !audio ? 0 : 1;
}
