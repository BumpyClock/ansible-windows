#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Media.Editing.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Streams.h>
#include <filesystem>
#include <cstdio>
#include <cstdlib>
using namespace winrt;
int wmain(int argc, wchar_t** argv) {
    if (argc != 3 && argc != 4) return 2;
    init_apartment(apartment_type::multi_threaded);
    try {
        auto input = Windows::Storage::StorageFile::GetFileFromPathAsync(argv[1]).get();
        auto clip = Windows::Media::Editing::MediaClip::CreateFromFileAsync(input).get();
        Windows::Media::Editing::MediaComposition composition;
        composition.Clips().Append(clip);
        auto milliseconds = argc == 4 ? std::wcstoll(argv[3], nullptr, 10) : 0;
        auto image = composition.GetThumbnailAsync(Windows::Foundation::TimeSpan{milliseconds * 10000}, 0, 0,
            Windows::Media::Editing::VideoFramePrecision::NearestFrame).get();
        std::filesystem::path path{argv[2]};
        auto folder = Windows::Storage::StorageFolder::GetFolderFromPathAsync(path.parent_path().c_str()).get();
        auto file = folder.CreateFileAsync(path.filename().c_str(),
            Windows::Storage::CreationCollisionOption::FailIfExists).get();
        auto stream = file.OpenAsync(Windows::Storage::FileAccessMode::ReadWrite).get();
        Windows::Storage::Streams::RandomAccessStream::CopyAsync(image, stream).get();
        stream.FlushAsync().get();
        stream.Close();
        wprintf(L"%s\n", image.ContentType().c_str());
        return 0;
    } catch (hresult_error const& error) {
        wprintf(L"0x%08X %s\n", static_cast<unsigned>(error.code().value), error.message().c_str());
        return 1;
    }
}
