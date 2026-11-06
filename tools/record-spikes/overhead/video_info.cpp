#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.FileProperties.h>
#include <cstdio>

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    try {
        auto file = winrt::Windows::Storage::StorageFile::GetFileFromPathAsync(argv[1]).get();
        auto properties = file.Properties().GetVideoPropertiesAsync().get();
        wprintf(L"width=%u height=%u duration100ns=%lld bitrate=%u\n", properties.Width(), properties.Height(),
            properties.Duration().count(), properties.Bitrate());
        return 0;
    } catch (winrt::hresult_error const& error) {
        wprintf(L"error=0x%08X\n", static_cast<unsigned>(error.code().value));
        return 1;
    }
}
