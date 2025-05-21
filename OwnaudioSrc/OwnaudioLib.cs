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
            string? relativeBase;
            Architecture cpuArchitec = RuntimeInformation.ProcessArchitecture;

            //Not a mobile system
            if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
            {
                relativeBase = DetermineDesktopRelativeBase();
            }
            else
            {
                relativeBase = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
            }

            if (string.IsNullOrEmpty(relativeBase))
                return false;

            pathPortAudio = System.IO.Path.Combine(relativeBase, ridext.Item1, "native", $"libportaudio.{ridext.Item2}");
            pathMiniAudio = System.IO.Path.Combine(relativeBase, ridext.Item1, "native", $"libminiaudio.{ridext.Item2}");

            //Ios system
            if (OperatingSystem.IsIOS())
            {
#if IOS
                string sourceFrameworkFolderInBundle = Path.Combine("runtimes", ridext.Item1, "native", "miniaudio.framework");
                string targetFrameworkSubFolder = Path.Combine(ridext.Item1, "native_copied", "miniaudio.framework");

                Console.WriteLine($"[INFO] IOS: Attempting to copy '{sourceFrameworkFolderInBundle}' from bundle to '{targetFrameworkSubFolder}' in app data.");

                try
                {
                    Ownaudio.Utilities.PlatformUtils.IOSBundleCopier.CopyBundleFolderToAppData(
                        sourceFolderNameInBundle: sourceFrameworkFolderInBundle,
                        targetSubFolderInAppData: targetFrameworkSubFolder,
                        overwrite: false // Állítsd 'true'-ra, ha mindig felül akarod írni.
                    );
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] OwnAudio.Initialize (iOS): Exception during IOSBundleCopier.CopyBundleFolderToAppData. {ex.Message}");
                    Console.WriteLine($"[ERROR] OwnAudio.Initialize (iOS): Exception during IOSBundleCopier.CopyBundleFolderToAppData. {ex.Message}");
                }

                string? appSpecificDataPath = Ownaudio.Utilities.PlatformUtils.GetAppSpecificBasePath(); //
                if (!string.IsNullOrEmpty(appSpecificDataPath))
                {
                    pathMiniAudio = Path.Combine(appSpecificDataPath, targetFrameworkSubFolder, "miniaudio");

                    if (!File.Exists(pathMiniAudio))
                    {
                        Debug.WriteLine($"[ERROR] OwnAudio.Initialize (iOS): miniaudio binary not found at '{pathMiniAudio}' after copy attempt.");
                        Console.WriteLine($"[ERROR] OwnAudio.Initialize (iOS): miniaudio binary not found at '{pathMiniAudio}' after copy attempt.");
                        pathMiniAudio = null; 
                    }
                    else
                    {
                        Debug.WriteLine($"[INFO] OwnAudio.Initialize (iOS): miniaudio path set to '{pathMiniAudio}'");
                        Console.WriteLine($"[INFO] OwnAudio.Initialize (iOS): miniaudio path set to '{pathMiniAudio}'");
                    }
                }
                else
                {
                    Debug.WriteLine("[ERROR] OwnAudio.Initialize (iOS): Failed to get app specific data path.");
                    Console.WriteLine("[ERROR] OwnAudio.Initialize (iOS): Failed to get app specific data path.");
                    pathMiniAudio = null;
                }
                pathPortAudio = "";
#endif
            }
            //Android system
            else if (OperatingSystem.IsAndroid())
            {
                pathMiniAudio = "libminiaudio";
                pathPortAudio = "";
            }
            //Macos System
            else if (OperatingSystem.IsMacOS())
            {
                if (cpuArchitec == Architecture.Arm64)
                    pathPortAudio = Path.Combine("/opt", "homebrew", "opt", "portaudio", "lib", $"libportaudio.{ridext.Item2}");
                else if (cpuArchitec == Architecture.X64)
                    pathPortAudio = Path.Combine("/usr", "local", "opt", "portaudio", "lib", $"libportaudio.{ridext.Item2}");
            }
            //Linux system
            else if (OperatingSystem.IsLinux())
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


            if (!File.Exists(pathPortAudio) && ffmpegPath is not null)
            {
                if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
                    pathPortAudio = Path.Combine(ffmpegPath, $"libportaudio.{ridext.Item2}");
            }

            try
            {
                PortAudioPath = pathPortAudio;
                MiniAudioPath = pathMiniAudio;
                LibraryPath = ffmpegPath;

                try
                {
                    InitializeMiniAudio(pathMiniAudio, hostType);
                    InitializePortAudio(pathPortAudio, hostType);
                    InitializeFFmpeg(ffmpegPath);
                }
                catch (Exception)
                {
                    Debug.WriteLine($"Audio initialize error.");
                }

                //IsFFmpegInitialized = false;
                //IsPortAudioInitialized = false;

                if (IsMiniAudioInitialized)
                {
                    return true;
                }
                else
                {
                    if (IsPortAudioInitialized && IsFFmpegInitialized)
                        return true;
                    else
                        return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return false;
            }
        }
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

    /// <summary>
    /// Defines the relative base path of the application
    /// </summary>
    /// <returns>Relative base path</returns>
    private static string? DetermineDesktopRelativeBase()
    {
        string? appCtxBaseDir = Ownaudio.Utilities.PlatformUtils.tGetAppSpecificBasePah();

        if (!string.IsNullOrEmpty(appCtxBaseDir))
        {
            string runtimesDirectPath = Path.Combine(appCtxBaseDir, "runtimes");
            if (Directory.Exists(runtimesDirectPath))
            {
                Console.WriteLine($"[INFO] Desktop: 'runtimes' folder found here: {runtimesDirectPath}");
                return runtimesDirectPath;
            }
            Console.WriteLine($"[INFO] Desktop: 'runtimes' folder not found here: '{runtimesDirectPath}'. Default base: '{appCtxBaseDir}'. Expected structure: '{appCtxBaseDir}/{{RID}}/native/'.");
            return appCtxBaseDir;
        }
        else
        {
            Console.WriteLine("[WARNING] Desktop: GetAppSpecificBasePath() returned null or empty value. Reverting to original 'runtimes' search logic (based on CurrentDirectory).");
            string? fallbackRelativeBase = Directory.Exists("runtimes") ? "runtimes" :
                                        Directory.EnumerateDirectories(Directory.GetCurrentDirectory(), "runtimes", SearchOption.AllDirectories)
                                        .Select(dirPath => Path.GetRelativePath(Directory.GetCurrentDirectory(), dirPath))
                                        .FirstOrDefault();
            if (string.IsNullOrEmpty(fallbackRelativeBase))
            {
                Debug.WriteLine("[ERROR] Desktop: Critical error - Unable to determine the value of relativeBase using either GetAppSpecificBasePath() or fallback logic.");
            }
            return fallbackRelativeBase;
        }
    }
}
