using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OwnaudioShowcase.Services;
using OwnaudioShowcase.ViewModels;
using OwnaudioShowcase.Views;

namespace OwnaudioShowcase;

public partial class App : Application
{
    // Singleton services
    public static IAudioEngineService AudioEngine { get; private set; } = null!;
    public static IFileDialogService FileDialog { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize services
        AudioEngine = new AudioEngineService();
        FileDialog = new FileDialogService();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };

            // Shutdown audio engine when application closes
            desktop.ShutdownRequested += async (s, e) =>
            {
                if (AudioEngine.IsInitialized)
                {
                    await AudioEngine.ShutdownAsync();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}