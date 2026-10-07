namespace Ansible;

internal sealed record AppPaths(
    string NativeLibrary, string ModelCatalog, string ModelsDirectory,
    string Usage, string StartupLog, string PublicSample, string Settings)
{
    public static AppPaths Create(string baseDirectory, string localDataDirectory)
    {
        return new(
            Path.Combine(baseDirectory, "audiocpp.dll"),
            Path.Combine(baseDirectory, "audio-models.json"),
            Path.Combine(localDataDirectory, "models"),
            Path.Combine(localDataDirectory, "usage.json"),
            Path.Combine(localDataDirectory, "startup-error.log"),
            Path.Combine(baseDirectory, "validation-sample.wav"),
            Path.Combine(localDataDirectory, "settings.json"));
    }
}
