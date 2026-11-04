#include "record_encoder.h"
#include <cstdio>
#include <cwchar>
int wmain(int argc, wchar_t** argv) {
    if (argc < 2) return 2;
    record_encoder_result result{};
    result.size = sizeof(result);
    bool fixture = argc > 2 && wcscmp(argv[2], L"--static-fixture") == 0;
    auto hr = fixture ? record_encoder_static_fixture(argv[1], 4, &result) :
        record_encoder_run(argv[1], 5, argc > 2 && wcscmp(argv[2], L"--borderless") == 0, nullptr, &result);
    wprintf(L"source=%s capture=0x%08X wgc_updates=%u size=%ux%u border=%d borderless=0x%08X qpc=%lld frequency=%lld first=%lld last=%lld\n",
        fixture ? L"synthetic-static-fixture" : L"monitor",
        hr, result.frames, result.width, result.height, result.border_required, result.borderless_hresult,
        result.start_qpc, result.qpc_frequency, result.first_frame_100ns, result.last_frame_100ns);
    wprintf(L"encoded_samples=%u held_samples=%u first_encode_qpc=%lld last_encode_qpc=%lld stop_qpc=%lld\n",
        result.encoded_samples, result.held_samples, result.first_encode_qpc, result.last_encode_qpc, result.stop_qpc);
    if (hr < 0) return 1;
    uint32_t frames{}, audio{};
    int64_t duration{};
    hr = record_encoder_inspect(argv[1], &frames, &audio, &duration);
    wprintf(L"inspect=0x%08X h264_samples=%u audio_streams=%u duration_100ns=%lld\n", hr, frames, audio, duration);
    if (hr < 0 || frames == 0 || audio != 0 || result.encoded_samples == 0) return 1;
    if (fixture && (result.frames != 0 || result.held_samples == 0 || duration < 39000000)) return 1;
    return 0;
}
