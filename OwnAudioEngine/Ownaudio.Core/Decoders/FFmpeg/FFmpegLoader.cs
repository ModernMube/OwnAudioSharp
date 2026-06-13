using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Logger;
using Ownaudio.Core;

namespace Ownaudio.Decoders.FFmpeg;

/// <summary>
/// AOT-compatible dynamic FFmpeg library loader.
/// Registers the DLL import resolver and validates successful loading
/// by probing the avutil library on first use.
/// </summary>
internal static class FFmpegLoader
{
    #region Fields

    /// <summary>
    /// Indicates whether the loader has already completed its one-time initialisation.
    /// Checked before acquiring the lock to avoid unnecessary synchronisation overhead.
    /// </summary>
    private static bool _initialized;

    /// <summary>
    /// Synchronisation object that serialises concurrent calls to <see cref="Initialize"/>
    /// so that the DLL resolver is registered exactly once.
    /// </summary>
    private static readonly object _lock = new();

    #endregion

    #region Initialization

    /// <summary>
    /// Initializes the FFmpeg loader and registers the DLL import resolver.
    /// Safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;

        lock (_lock)
        {
            if (_initialized)
                return;

            _initialized = true;

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(FFmpegLoader).Assembly, DllImportResolver);

                IntPtr handle = LoadLibrary("avutil");
                if (handle == IntPtr.Zero)
                {
                    Log.Info("FFmpeg (avutil) not found — built-in decoder will be used.");
                    return;
                }

                FFmpegConfig.IsAvailable = true;
                Log.Info("FFmpeg loaded successfully.");
            }
            catch (Exception ex)
            {
                Log.Error($"FFmpeg initialization failed: {ex.Message}");
                FFmpegConfig.IsAvailable = false;
            }
        }
    }

    /// <summary>
    /// Custom DLL import resolver registered with <see cref="NativeLibrary.SetDllImportResolver"/>.
    /// Intercepts P/Invoke resolution for FFmpeg libraries and delegates to <see cref="LoadLibrary"/>.
    /// </summary>
    /// <param name="libraryName">The name of the native library being resolved.</param>
    /// <param name="assembly">The assembly that contains the P/Invoke declaration.</param>
    /// <param name="searchPath">Optional search path hint provided by the runtime.</param>
    /// <returns>
    /// A valid library handle when the name is an FFmpeg library; <see cref="IntPtr.Zero"/> otherwise.
    /// </returns>
    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!IsFFmpegLibrary(libraryName))
            return IntPtr.Zero;

        return LoadLibrary(libraryName);
    }

    #endregion

    #region Library Loading

    /// <summary>
    /// Loads an FFmpeg dynamic library from the configured custom path
    /// or from standard system locations for the current platform.
    /// </summary>
    /// <param name="baseName">
    /// The base library name without prefix or extension (e.g. "avutil").
    /// </param>
    /// <returns>
    /// A valid native library handle, or <see cref="IntPtr.Zero"/> if the library was not found.
    /// </returns>
    internal static IntPtr LoadLibrary(string baseName)
    {
        if (!string.IsNullOrEmpty(FFmpegConfig.CustomLibraryPath))
        {
            IntPtr h = TryLoadFromDirectory(FFmpegConfig.CustomLibraryPath, baseName);
            if (h != IntPtr.Zero)
                return h;
        }

        IntPtr fromBase = TryLoadFromDirectory(AppContext.BaseDirectory, baseName);
        if (fromBase != IntPtr.Zero)
            return fromBase;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return TryLoadWindows(baseName);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return TryLoadMacOS(baseName);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return TryLoadLinux(baseName);

        return IntPtr.Zero;
    }

    /// <summary>
    /// Attempts to load a versioned or plain FFmpeg DLL on Windows.
    /// Tries suffixed names from version 62 down to 58 before falling back to the unversioned DLL name.
    /// </summary>
    /// <param name="baseName">Base library name without extension (e.g. "avformat").</param>
    /// <returns>A valid handle, or <see cref="IntPtr.Zero"/> if no candidate loaded.</returns>
    private static IntPtr TryLoadWindows(string baseName)
    {
        for (int v = 62; v >= 58; v--)
        {
            if (NativeLibrary.TryLoad($"{baseName}-{v}.dll", out IntPtr h))
                return h;
        }

        if (NativeLibrary.TryLoad($"{baseName}.dll", out IntPtr plain))
            return plain;

        return IntPtr.Zero;
    }

    /// <summary>
    /// Attempts to load an FFmpeg dylib from common Homebrew and system library
    /// directories on macOS.
    /// </summary>
    /// <param name="baseName">Base library name without prefix or extension (e.g. "avutil").</param>
    /// <returns>A valid handle, or <see cref="IntPtr.Zero"/> if no candidate loaded.</returns>
    private static IntPtr TryLoadMacOS(string baseName)
    {
        string[] prefixes = { "/opt/homebrew/lib", "/usr/local/lib", "/usr/lib" };

        foreach (string dir in prefixes)
        {
            IntPtr h = TryLoadFromDirectory(dir, baseName);
            if (h != IntPtr.Zero)
                return h;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Attempts to load an FFmpeg shared object from architecture-specific and
    /// standard system library directories on Linux.
    /// </summary>
    /// <param name="baseName">Base library name without prefix or extension (e.g. "avcodec").</param>
    /// <returns>A valid handle, or <see cref="IntPtr.Zero"/> if no candidate loaded.</returns>
    private static IntPtr TryLoadLinux(string baseName)
    {
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "aarch64-linux-gnu",
            Architecture.Arm   => "arm-linux-gnueabihf",
            _                  => "x86_64-linux-gnu"
        };

        string[] prefixes =
        {
            $"/usr/lib/{arch}",
            "/usr/lib",
            "/usr/local/lib",
            "/usr/lib64"
        };

        foreach (string dir in prefixes)
        {
            IntPtr h = TryLoadFromDirectory(dir, baseName);
            if (h != IntPtr.Zero)
                return h;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Scans a single directory for any file whose name matches the expected platform
    /// library naming pattern and attempts to load the first successful candidate.
    /// </summary>
    /// <param name="directory">Absolute path of the directory to search.</param>
    /// <param name="baseName">Base library name without prefix or extension.</param>
    /// <returns>A valid handle if a matching file loaded; otherwise <see cref="IntPtr.Zero"/>.</returns>
    private static IntPtr TryLoadFromDirectory(string directory, string baseName)
    {
        if (!Directory.Exists(directory))
            return IntPtr.Zero;

        string ext       = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll"   :
                           RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? ".dylib" : ".so";
        string libPrefix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ""       : "lib";

        try
        {
            string[] candidates = Directory.GetFiles(directory, $"{libPrefix}{baseName}*{ext}*");
            foreach (string candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, out IntPtr h))
                    return h;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string[] soVersioned = Directory.GetFiles(directory, $"{libPrefix}{baseName}.so.*");
                foreach (string candidate in soVersioned)
                {
                    if (NativeLibrary.TryLoad(candidate, out IntPtr h))
                        return h;
                }
            }
        }
        catch
        {
        }

        return IntPtr.Zero;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Returns true when the given library name is one of the four FFmpeg modules
    /// handled by this loader: avcodec, avformat, avutil, or swresample.
    /// </summary>
    /// <param name="name">The library name to test.</param>
    /// <returns>
    /// <c>true</c> if the name is an FFmpeg library; <c>false</c> otherwise.
    /// </returns>
    private static bool IsFFmpegLibrary(string name)
    {
        return name is "avcodec" or "avformat" or "avutil" or "swresample";
    }

    #endregion
}
