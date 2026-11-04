#include "record_encoder.h"
#include <cstdio>
#include <cwchar>
int wmain(int argc, wchar_t** argv) {
    if (argc < 2) return 2;
    record_encoder_result result{};
    result.size = sizeof(result);
    auto hr = record_encoder_run(argv[1], 5, argc > 2 && wcscmp(argv[2], L"--borderless") == 0,
        nullptr, &result);
    wprintf(L"capture=0x%08X frames=%u size=%ux%u border=%d borderless=0x%08X qpc=%lld frequency=%lld first=%lld last=%lld\n",
        hr, result.frames, result.width, result.height, result.border_required, result.borderless_hresult,
        result.start_qpc, result.qpc_frequency, result.first_frame_100ns, result.last_frame_100ns);
    if (hr < 0) return 1;
    uint32_t frames{}, audio{};
    int64_t duration{};
    hr = record_encoder_inspect(argv[1], &frames, &audio, &duration);
    wprintf(L"inspect=0x%08X h264_samples=%u audio_streams=%u duration_100ns=%lld\n", hr, frames, audio, duration);
    return hr < 0 || frames == 0 || audio != 0 ? 1 : 0;
}
