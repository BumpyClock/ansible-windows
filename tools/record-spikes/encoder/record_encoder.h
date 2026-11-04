#pragma once
#include <stdint.h>

// Spike ABI. The caller owns the worker and joins this blocking operation.
struct record_encoder_result {
    uint32_t size;
    uint32_t frames;
    uint32_t width;
    uint32_t height;
    int32_t border_required;
    int32_t borderless_hresult;
    int64_t start_qpc;
    int64_t qpc_frequency;
    int64_t first_frame_100ns;
    int64_t last_frame_100ns;
};
extern "C" __declspec(dllexport) int32_t __cdecl record_encoder_run(
    const wchar_t* output, uint32_t seconds, int32_t request_borderless,
    void* cancellation_event, record_encoder_result* result);
extern "C" __declspec(dllexport) int32_t __cdecl record_encoder_inspect(
    const wchar_t* input, uint32_t* video_frames, uint32_t* audio_streams,
    int64_t* duration_100ns);
