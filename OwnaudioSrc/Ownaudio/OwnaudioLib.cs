using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Diagnostics;

using Ownaudio.Bindings.PortAudio;
using Ownaudio.Exceptions;
using Ownaudio.Utilities;
using Ownaudio.Engines;

using FFmpeg.AutoGen;

namespace Ownaudio;
/// <summary>
/// Functions to retrieve, configure and manage the current Ownaudio environment, 
/// which affects the entire directory configuration.
/// </summary>
public static partial class OwnAudio
{
    private static readonly object _initLock = new object();

    /// <summary>
    /// We set the basic parameters related to sound processing.
    /// </summary>
    internal static class Constants
    {
        public const AVSampleFormat FFmpegSampleFormat = AVSampleFormat.AV_SAMPLE_FMT_FLT;
        public const PaBinding.PaSampleFormat PaSampleFormat = PaBinding.PaSampleFormat.paFloat32;
    }
    
    /// <summary>
    /// Initialize and register the PortAudio library and 
    /// initialize and register the FFmpeg functions by specifying the path to the FFmpeg native libraries. 
    /// Leave the directory parameter blank to use system-level directories. 
    /// Exits if already initialized.
    /// </summary>
    /// <param name="ffmpegPath">Path to FFmpeg native libraries, leave blank to use system level libraries.</param>
    /// <param name="hostType">Sets the audio api to be used.</param>
    public static bool Initialize(string? ffmpegPath, OwnAudioEngine.EngineHostType hostType = OwnAudioEngine.EngineHostType.None)
    {
        lock (_initLock)
        {
            var ridext = GetRidAndLibExtensions();
            string? pathPortAudio;
            Architecture cpuArchitec = RuntimeInformation.ProcessArchitecture;

            pathPortAudio = System.IO.Path.Combine("libs", ridext.Item1, $"libportaudio.{ridext.Item2}");

            if (!File.Exists(pathPortAudio) && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                if (cpuArchitec == Architecture.Arm64)
                    pathPortAudio = Path.Combine("/opt", "local", "lib", $"libportaudio.{ridext.Item2}");
                else if (cpuArchitec == Architecture.X64)
                    pathPortAudio = Path.Combine("/opt", "local", "lib", $"libportaudio.{ridext.Item2}");

            if (!File.Exists(pathPortAudio) && ffmpegPath is not null)
                pathPortAudio = Path.Combine(ffmpegPath, $"libportaudio.{ridext.Item2}");

            try
            {
                PortAudioPath = pathPortAudio;
                FFmpegPath = ffmpegPath;

                Ensure.That<OwnaudioException>(File.Exists(pathPortAudio), "AudioEngine is not initialized.");

                InitializePortAudio(pathPortAudio, hostType);
                try
                {
                    InitializeFFmpeg(ffmpegPath);
                }
                catch (Exception)
                {
                    // If FFmpeg initialization throws an error, free up PortAudio resources
                    Free();
                    throw;
                }

                if (IsFFmpegInitialized && IsPortAudioInitialized)
                    return true;
                else
                    return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// Initialize and register the PortAudio library and initialize and 
    /// register the FFmpeg functions with the FFmpeg native libraries, 
    /// the system default directory.
    /// </summary>
    public static bool Initialize()
    {
        IPlatformPathProvider _platform = GetPlatformProvider();
        return Initialize(_platform.GetFFmpegPath()); 
    }

    /// <summary>
    /// Initialize and register the PortAudio library and initialize 
    /// and register the FFmpeg functions with the FFmpeg native libraries, the system default director 
    /// and sets the audio api to use.
    /// </summary>
    /// <param name="hostType">Host API type</param>
    /// <returns></returns>
    public static bool Initialize(OwnAudioEngine.EngineHostType hostType)
    {
        IPlatformPathProvider _platform = GetPlatformProvider();
        return Initialize(_platform.GetFFmpegPath(), hostType);
    }
   
    /// <summary>
    /// Specifies to access and extend the current system's PortAudio library. 
    /// </summary>
    /// <returns>The name of the directory and the system-dependent file extension</returns>
    /// <exception cref="NotSupportedException"></exception>
    private static (string, string) GetRidAndLibExtensions()
    {
        if (PlatformInfo.IsWindows)
        {
            var rid = Environment.Is64BitOperatingSystem ? "win-x64" : "win-x86";
            return (rid, "dll");
        }
        else if (PlatformInfo.IsLinux)
        {
            return ("linux-x64", "so.2");
        }
        else if (PlatformInfo.IsOSX)
        {
            return ("osx-x64", "dylib");
        }
        else
        {
            throw new NotSupportedException();
        }
    }
}
