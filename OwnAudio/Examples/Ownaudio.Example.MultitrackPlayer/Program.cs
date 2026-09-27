using Avalonia;
using System;

namespace MultitrackPlayer;

/// <summary>
/// Entry point, plain Avalonia desktop start.
/// </summary>
class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// Also what the designer calls.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
