#pragma once
#include <cstdint>

// All timestamps use QueryPerformanceCounter, including the accepted-chord origin.
// create is allocation only. run blocks, owns its workers, and joins before return.
// stop/status/burst are safe while run executes. destroy refuses a running take.
struct record_take_status {
    uint32_t size;
    uint32_t running;
    uint32_t discarded;
    uint32_t burst_count;
    uint32_t picture_count;
    uint32_t width;
    uint32_t height;
    uint32_t summary_saved;
    int32_t capture_error;
    int32_t ink_error;
    int32_t video_error;
    int32_t frame_error;
    int64_t first_video_qpc;
    int64_t stop_qpc;
    uint32_t source_frames;
    uint32_t encoded_samples;
    uint32_t held_samples;
    uint32_t video_finalized;
    uint32_t mouse_events;
    uint32_t mouse_moves;
    uint32_t max_hook_delay_ms;
    uint32_t hook_delays_over_100ms;
    uint32_t max_present_us;
    uint32_t max_loop_us;
    uint32_t max_fence_wait_ms;
    uint32_t hooks_released_before_join;
};
struct record_take_burst {
    uint32_t size;
    uint32_t number;
    uint32_t frame_saved;
    uint32_t reserved;
    int64_t timestamp;
};
static_assert(sizeof(record_take_status) == 112);
static_assert(sizeof(record_take_burst) == 24);

extern "C" __declspec(dllexport) int32_t __cdecl record_take_create(
    void* monitor, const wchar_t* directory, int64_t origin_qpc, void** take);
extern "C" __declspec(dllexport) int32_t __cdecl record_take_run(void* take);
extern "C" __declspec(dllexport) int32_t __cdecl record_take_stop(void* take, int32_t discard);
extern "C" __declspec(dllexport) int32_t __cdecl record_take_get_status(void* take, record_take_status* status);
extern "C" __declspec(dllexport) int32_t __cdecl record_take_get_burst(void* take, uint32_t index, record_take_burst* burst);
extern "C" __declspec(dllexport) int32_t __cdecl record_take_destroy(void* take);
