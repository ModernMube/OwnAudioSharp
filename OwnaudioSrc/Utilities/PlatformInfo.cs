using System.Runtime.InteropServices;

namespace Ownaudio.Utilities;

/// <summary>
/// Get information about the current platform.
/// </summary>
internal static class PlatformInfo
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsOSX => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
}
