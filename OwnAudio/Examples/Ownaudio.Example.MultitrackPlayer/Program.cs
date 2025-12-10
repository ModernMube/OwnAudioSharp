using Avalonia;
using System;

namespace MultitrackPlayer;

/// <summary>
/// Entry point class for the multitrack audio player application.
/// Configures and starts the Avalonia application framework.
/// </summary>
class Program
{
    #region Entry Point

    /// <summary>
    /// Main entry point for the application.
    /// Initializes the Avalonia framework and starts the desktop application.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <remarks>
    /// Do not use any Avalonia, third-party APIs, or SynchronizationContext-reliant code
    /// before this method is called, as the framework is not yet initialized.
    /// </remarks>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    #endregion

    #region Avalonia Configuration

    /// <summary>
    /// Configures the Avalonia application framework.
    /// This method is also used by the visual designer.
    /// </summary>
    /// <returns>A configured AppBuilder instance.</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    #endregion
}
