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
        string GetPortAudioLibName();
    }

    private class WindowsPathProvider : IPlatformPathProvider
    {
        // avcodec-61.dll
        public string GetFFmpegPath()
        {
            string pathFFmpeg = Path.Combine("Libs", "win-x64");
            if (!File.Exists(Path.Combine(pathFFmpeg, "avcodec-61.dll")))
                pathFFmpeg = Path.Combine("C:", "ffmpeg", "bin");
            return pathFFmpeg;
        }

        public string GetPortAudioLibName() => "portaudio.dll";
    }

    private class OSXPathProvider : IPlatformPathProvider
    {
        // libavcodec.61.dylib
        public string GetFFmpegPath()
        {
            string pathFFmpeg = Path.Combine("Libs", "osx-x64");
            if (!File.Exists(Path.Combine(pathFFmpeg, "libavcodec.61.dylib")))
            {
                if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                    pathFFmpeg = System.IO.Path.Combine("/opt", "local", "lib");
                else if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                    pathFFmpeg = System.IO.Path.Combine("/opt", "local", "libexec", "ffmpeg7", "lib");                     
            }
            return pathFFmpeg;
        }

        public string GetPortAudioLibName() => "libportaudio.dylib";
    }

    private class LinuxPathProvider : IPlatformPathProvider
    {
        // libavcodec.so.61
        public string GetFFmpegPath()
        {
            string pathFFmpeg = Path.Combine("Libs", "linux-x64");
            if (!File.Exists(Path.Combine(pathFFmpeg, "libavcodec.so.61")))
                pathFFmpeg = Path.Combine("/usr", "local", "lib");
            return pathFFmpeg;
        }

        public string GetPortAudioLibName() => "libportaudio.so.2";
    }

    private static IPlatformPathProvider GetPlatformProvider()
    {
        if (PlatformInfo.IsWindows) return new WindowsPathProvider();
        if (PlatformInfo.IsLinux) return new LinuxPathProvider();
        if (PlatformInfo.IsOSX) return new OSXPathProvider();
        throw new NotSupportedException("Platform not supported");
    }
}


