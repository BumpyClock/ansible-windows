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
        LocalDiagnosticLog? diagnostics = null;
        long? launchGeneration = null;
        long? launchStarted = null;
        try
        {
            var paths = AppPaths.Create(AppContext.BaseDirectory, LocalDataDirectory);
            var microphonePermission = new MicrophonePermissionPreflight(new WindowsMicrophonePermission());
            var packageVersion = Windows.ApplicationModel.Package.Current.Id.Version;
            diagnostics = new LocalDiagnosticLog(Path.Combine(Path.GetDirectoryName(paths.Settings)!, "diagnostics"),
                new Version(packageVersion.Major, packageVersion.Minor, packageVersion.Build, packageVersion.Revision));
            var session = new DictationSession(
                directory => new NativeAudioEngine(paths.NativeLibrary, paths.ModelCatalog, directory,
                    deviceProbePath: paths.DeviceProbe, benchmarkSamplePath: paths.PublicSample,
                    benchmarkCachePath: paths.DeviceBenchmarks),
                new PermissionCheckedAudioCaptureFactory(new WaveInCaptureFactory(), microphonePermission), new AudioInputReader(), new UsageStore(paths.Usage), paths.ModelsDirectory,
                settingsStore: new AppSettingsStore(paths.Settings), diagnostics: diagnostics);
            launchGeneration = diagnostics.CurrentGeneration;
            if (launchGeneration is not null) launchStarted = Stopwatch.GetTimestamp();
            Window = new MainWindow(session, paths, ModelDownloadManager.CreateHttpClient(), microphonePermission, diagnostics);
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
            if (launchGeneration is { } generation && launchStarted is { } started)
                diagnostics.Write(new ApplicationDiagnostic(DateTimeOffset.UtcNow, ApplicationDiagnosticStage.Launch,
                    DiagnosticOutcome.Completed, Stopwatch.GetElapsedTime(started).TotalMilliseconds), generation);
        }
        catch (Exception error)
        {
            if (diagnostics?.CurrentGeneration is { } generation)
            {
                var elapsed = launchGeneration == generation && launchStarted is { } started
                    ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : (double?)null;
                diagnostics.Write(new ApplicationDiagnostic(DateTimeOffset.UtcNow, ApplicationDiagnosticStage.Launch,
                    DiagnosticOutcome.Failed, elapsed, error.HResult), generation);
                _ = diagnostics.DisposeAsync().AsTask();
            }
            Debug.WriteLine($"Ansible startup failed, HRESULT 0x{error.HResult:X8}.");
            throw;
        }
    }
}
