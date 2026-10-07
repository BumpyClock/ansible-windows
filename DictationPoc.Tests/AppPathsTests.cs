using DictationPoc;

namespace DictationPoc.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void PathsUsePackageResourcesAndPerUserSettingsAndData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalVoicePathsTests");
        var baseDirectory = Path.Combine(directory, "app");
        var localData = Path.Combine(directory, "user");
        var paths = AppPaths.Create(baseDirectory, localData);
        Assert.Equal(Path.Combine(localData, "models"), paths.ModelsDirectory);
        Assert.Equal(Path.Combine(baseDirectory, "audio-models.json"), paths.ModelCatalog);
        Assert.Equal(Path.Combine(localData, "settings.json"), paths.Settings);
        Assert.Equal(Path.Combine(localData, "usage.json"), paths.Usage);
    }
}
