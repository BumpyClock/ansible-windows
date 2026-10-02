using Microsoft.UI.Xaml;
using System.Diagnostics;
using DictationPoc.Core;

namespace DictationPoc;

public partial class App : Application
{
    internal MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var paths = AppPaths.Create(AppContext.BaseDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalVoice"));
            var session = new DictationSession(
                directory => new NativeAudioEngine(paths.NativeLibrary, paths.ModelCatalog, directory),
                new WaveInCaptureFactory(), new AudioInputReader(), new UsageStore(paths.Usage), paths.ModelsDirectory);
            Window = new MainWindow(session, paths, ModelDownloadManager.CreateHttpClient());
            Window.Activate();
        }
        catch (Exception error)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalVoice");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "startup-error.log"), error.ToString());
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Cannot write startup diagnostics: {logError.Message}. Original failure: {error}");
            }
            throw;
        }
    }
}
