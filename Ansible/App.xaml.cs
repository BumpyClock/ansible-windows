using Microsoft.UI.Xaml;
using System.Diagnostics;
using Ansible.Core;

namespace Ansible;

public partial class App : Application
{
    internal MainWindow? Window { get; private set; }
    private static string LocalDataDirectory => Windows.Storage.ApplicationData.Current.LocalFolder.Path;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var paths = AppPaths.Create(AppContext.BaseDirectory, LocalDataDirectory);
            var microphonePermission = new MicrophonePermissionPreflight(new WindowsMicrophonePermission());
            var session = new DictationSession(
                directory => new NativeAudioEngine(paths.NativeLibrary, paths.ModelCatalog, directory,
                    deviceProbePath: paths.DeviceProbe, benchmarkSamplePath: paths.PublicSample,
                    benchmarkCachePath: paths.DeviceBenchmarks),
                new PermissionCheckedAudioCaptureFactory(new WaveInCaptureFactory(), microphonePermission), new AudioInputReader(), new UsageStore(paths.Usage), paths.ModelsDirectory,
                settingsStore: new AppSettingsStore(paths.Settings));
            Window = new MainWindow(session, paths, ModelDownloadManager.CreateHttpClient(), microphonePermission);
#if DEBUG
            // Scope screenshot qualification to this window without changing Windows appearance.
            if (Window.Content is FrameworkElement previewRoot)
                previewRoot.RequestedTheme = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() switch
                {
                    "--design-theme=light" => ElementTheme.Light,
                    "--design-theme=dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
#endif
            Window.Activate();
        }
        catch (Exception error)
        {
            try
            {
                var directory = LocalDataDirectory;
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
