using DictationPoc;

namespace DictationPoc.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LocalVoicePathsTests", Guid.NewGuid().ToString("N"));
    private string BaseDirectory => Path.Combine(_directory, "app");
    private string LocalData => Path.Combine(_directory, "user");

    public AppPathsTests() => Directory.CreateDirectory(BaseDirectory);

    [Fact]
    public void CleanInstallDefaultsToPerUserModels()
    {
        var paths = AppPaths.Create(BaseDirectory, LocalData);
        Assert.Equal(Path.Combine(LocalData, "models"), paths.ModelsDirectory);
        Assert.Equal(Path.Combine(BaseDirectory, "audio-models.json"), paths.ModelCatalog);
    }

    [Fact]
    public void DeploymentConfigurationCanSelectTheDevelopmentModels()
    {
        File.WriteAllText(Path.Combine(BaseDirectory, "runtime-settings.json"), """{"models_directory":"weights"}""");
        Assert.Equal(Path.Combine(BaseDirectory, "weights"), AppPaths.Create(BaseDirectory, LocalData).ModelsDirectory);
    }

    [Fact]
    public async Task UserChosenFolderPersistsAndTakesPrecedenceWithoutMovingWeights()
    {
        File.WriteAllText(Path.Combine(BaseDirectory, "runtime-settings.json"), """{"models_directory":"weights"}""");
        var paths = AppPaths.Create(BaseDirectory, LocalData);
        var choice = Path.Combine(_directory, "chosen");
        await paths.SaveModelsDirectoryAsync(choice, CancellationToken.None);
        Assert.Equal(choice, AppPaths.Create(BaseDirectory, LocalData).ModelsDirectory);
        Assert.False(Directory.Exists(choice));
        Assert.False(File.Exists(paths.ModelPreferences + ".tmp"));
    }

    [Fact]
    public async Task CancelledSaveRetainsPreviousFolderPreference()
    {
        var paths = AppPaths.Create(BaseDirectory, LocalData);
        var first = Path.Combine(_directory, "first");
        await paths.SaveModelsDirectoryAsync(first, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            paths.SaveModelsDirectoryAsync(Path.Combine(_directory, "second"), cancellation.Token));
        Assert.Equal(first, AppPaths.Create(BaseDirectory, LocalData).ModelsDirectory);
        Assert.False(File.Exists(paths.ModelPreferences + ".tmp"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
