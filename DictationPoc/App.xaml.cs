using Microsoft.UI.Xaml;
using System.Diagnostics;
using DictationPoc.Services;
using Microsoft.UI.Dispatching;

namespace DictationPoc;

public partial class App : Application
{
    internal MainWindow? Window { get; private set; }
    internal DictationController Controller { get; private set; } = null!;

    public App()
    {
        RequestedTheme = ApplicationTheme.Light;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Controller = new DictationController(DispatcherQueue.GetForCurrentThread());
            Window = new MainWindow();
            Window.Activate();
        }
        catch (Exception error)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), error.ToString());
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Cannot write startup diagnostics: {logError.Message}. Original failure: {error}");
            }
            throw;
        }
    }
}
