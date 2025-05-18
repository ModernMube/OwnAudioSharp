using System.Runtime.InteropServices;
using System.IO;

using Ownaudio.Utilities;

using FFmpeg.AutoGen;
using System;

namespace Ownaudio;

/// <summary>
/// Functions to retrieve, configure and manage the current Ownaudio environment, 
/// which affects the entire directory configuration.
/// </summary>
public static partial class OwnAudio
{
    /// <summary>
    /// Initialize and register FFmpeg functions by specifying the path to FFmpeg native libraries. 
    /// Leave the directory parameter blank to use system-level directories. 
    /// Exits if already initialized.
    /// </summary>
    /// <param name="ffmpegDirectory">Path to FFmpeg native libraries, leave blank to use system level libraries.</param>
    private static void InitializeFFmpeg(string? ffmpegDirectory = default)
    {
        if (IsFFmpegInitialized)
        {
            return;
        }

        ffmpeg.RootPath = ffmpegDirectory ?? "";
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
        IsFFmpegInitialized = true;
    }

    private interface IPlatformPathProvider
    {
        string GetFFmpegPath();
    }

    private class WindowsPathProvider : IPlatformPathProvider
    {
        // avcodec-61.dll
        public string GetFFmpegPath()
        {
            string pathFFmpeg = Path.Combine("Libs", "win-x64");
            if (!File.Exists(Path.Combine(pathFFmpeg, "avcodec-60.dll")))
                pathFFmpeg = Path.Combine("C:", "ffmpeg", "bin");
            return pathFFmpeg;
        }
    }

    private class OSXPathProvider : IPlatformPathProvider
    {
        // libavcodec.61.dylib
        public string GetFFmpegPath()
        {
            string pathFFmpeg = Path.Combine("Libs", "osx-x64");
            if (!File.Exists(Path.Combine(pathFFmpeg, "libavcodec.60.dylib")))
            {
                if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                    pathFFmpeg = System.IO.Path.Combine("/opt", "homebrew", "opt", "ffmpeg@6","lib");
                else if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                    pathFFmpeg = System.IO.Path.Combine("/usr", "local", "opt", "ffmpeg@6","lib");                     
            }
            return pathFFmpeg;
        }
    }

    private class LinuxPathProvider : IPlatformPathProvider
    {
        // libavcodec.so.61
        public string GetFFmpegPath()
        {
            string pathFFmpeg = Path.Combine("Libs", "linux-x64");
            if (!File.Exists(Path.Combine(pathFFmpeg, "libavcodec.so.60")))
            {
                Architecture cpuArchitec = RuntimeInformation.ProcessArchitecture;
                switch (cpuArchitec)
                {
                    case Architecture.Arm:
                        pathFFmpeg= Path.Combine("/usr/lib", "arm-linux-gnueabihf");
                        break;
                    case Architecture.Arm64:
                        pathFFmpeg = Path.Combine("/usr/lib", "aarch64-linux-gnu");
                        break;
                    case Architecture.X64:
                        pathFFmpeg = Path.Combine("/usr/lib", "x86_64-linux-gnu");
                        break;    
                    case Architecture.X86:
                        pathFFmpeg = Path.Combine("/usr/lib", "i386-linux-gnu");
                        break; 
                    default:    
                        pathFFmpeg = Path.Combine("/usr/lib");
                        break; 
                }
            }
            return pathFFmpeg;
        }
    }

    private static IPlatformPathProvider GetPlatformProvider()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPathProvider();
        if (OperatingSystem.IsLinux()) return new LinuxPathProvider();
        if (OperatingSystem.IsMacOS()) return new OSXPathProvider();
        throw new NotSupportedException("Platform not supported");
    }
}


