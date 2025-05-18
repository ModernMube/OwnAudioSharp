using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Diagnostics;

using Ownaudio.Bindings.PortAudio;
using Ownaudio.Engines;

using FFmpeg.AutoGen;
using System.Linq;

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
            string? pathMiniAudio;
            Architecture cpuArchitec = RuntimeInformation.ProcessArchitecture;

            string? relativeBase = Directory.Exists("runtimes") ? "runtimes" :
                Directory.EnumerateDirectories(Directory.GetCurrentDirectory(), "runtimes", SearchOption.AllDirectories)
                    .Select(dirPath => Path.GetRelativePath(Directory.GetCurrentDirectory(), dirPath))
                    .FirstOrDefault();

            if(string.IsNullOrEmpty(relativeBase))
                return false;

            pathPortAudio = System.IO.Path.Combine(relativeBase, ridext.Item1, "native", $"libportaudio.{ridext.Item2}");
            pathMiniAudio = System.IO.Path.Combine(relativeBase, ridext.Item1, "native", $"libminiaudio.{ridext.Item2}");

            if(!File.Exists(pathMiniAudio) && OperatingSystem.IsIOS())
                pathMiniAudio = System.IO.Path.Combine(relativeBase, ridext.Item1, "native", "miniaudio.framework", "miniaudio");

            if (!File.Exists(pathPortAudio) && OperatingSystem.IsMacOS())
                if (cpuArchitec == Architecture.Arm64)
                    pathPortAudio = Path.Combine("/opt", "homebrew", "opt", "portaudio","lib", $"libportaudio.{ridext.Item2}");
                else if (cpuArchitec == Architecture.X64)
                    pathPortAudio = Path.Combine("/usr", "local", "opt", "portaudio", "lib", $"libportaudio.{ridext.Item2}");

            if (!File.Exists(pathPortAudio) && ffmpegPath is not null)
            {
                pathPortAudio = Path.Combine(ffmpegPath, $"libportaudio.{ridext.Item2}");
                if (OperatingSystem.IsLinux())
                {
                    switch (cpuArchitec)
                    {
                        case Architecture.Arm:
                            pathPortAudio = Path.Combine("/usr/lib", "arm-linux-gnueabihf", $"libportaudio.{ridext.Item2}.2");
                            break;
                        case Architecture.Arm64:
                            pathPortAudio = Path.Combine("/usr/lib", "aarch64-linux-gnu", $"libportaudio.{ridext.Item2}.2");
                            break;
                        case Architecture.X64:
                            pathPortAudio = Path.Combine("/usr/lib", "x86_64-linux-gnu", $"libportaudio.{ridext.Item2}.2");
                            break;    
                        case Architecture.X86:
                            pathPortAudio = Path.Combine("/usr/lib", "i386-linux-gnu", $"libportaudio.{ridext.Item2}.2");
                            break; 
                        default:    
                            pathPortAudio = Path.Combine("/usr/lib", $"libportaudio.{ridext.Item2}.2");
                            break; 
                    }
                }
            }
            
            try
            {
                PortAudioPath = pathPortAudio;      
                MiniAudioPath = pathMiniAudio;
                LibraryPath = ffmpegPath;
                
                try
                {
                    InitializeMa(pathMiniAudio, hostType);
                    InitializePortAudio(pathPortAudio, hostType);
                    
                    InitializeFFmpeg(ffmpegPath);
                }
                catch (Exception)
                {
                    Debug.WriteLine("Audio initialize error.");
                }

                //IsFFmpegInitialized = false;
                //IsPortAudioInitialized = false;
                
                if (IsMiniAudioInitialized)
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
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => ("win-x64", "dll"),
                Architecture.X86 => ("win-x86", "dll"),
                Architecture.Arm64 => ("win-arm64", "dll"),
                _ => throw new PlatformNotSupportedException(
                        $"Unsupported Windows architecture: {RuntimeInformation.ProcessArchitecture}")
            };
        }
        else if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => ("linux-x64", "so"),
                Architecture.Arm => ("linux-arm", "so"),
                Architecture.Arm64 => ("linux-arm64", "so"),
                _ => throw new PlatformNotSupportedException(
                        $"unsupported Linux architecture: {RuntimeInformation.ProcessArchitecture}"),
            };
        }
        else if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => ("osx-x64", "dylib"),
                Architecture.Arm64 => ("osx-arm64", "dylib"),
                _ => throw new PlatformNotSupportedException(
                        $"unsupported MacOS architecture: {RuntimeInformation.ProcessArchitecture}"),
            };
        }
        else if (OperatingSystem.IsAndroid())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => ("android-x64", "so"),
                Architecture.Arm => ("android-arm", "so"),
                Architecture.Arm64 => ("android-arm64", "so"),
                _ => throw new PlatformNotSupportedException(
                        $"unsupported Android architecture: {RuntimeInformation.ProcessArchitecture}"),
            };
        }
        else if (OperatingSystem.IsIOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => ("ios-arm64", ""),
                _ => throw new PlatformNotSupportedException(
                        $"unsupported IOS architecture: {RuntimeInformation.ProcessArchitecture}"),
            };
        }
        else
        {
            throw new NotSupportedException();
        }
    }
}
