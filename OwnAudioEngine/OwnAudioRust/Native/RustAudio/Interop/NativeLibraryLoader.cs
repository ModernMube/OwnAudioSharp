using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Resolves the native <c>ownaudio_ffi</c> library across platforms using
/// <see cref="NativeLibrary.SetDllImportResolver"/>.
/// Must be registered once before any <c>LibraryImport</c> call is made.
/// Call <see cref="EnsureRegistered"/> from the static constructor of
/// <see cref="OwnAudioNative"/>.
/// </summary>
internal static class NativeLibraryLoader
{
    /// <summary>
    /// The logical library name used in every <c>[LibraryImport]</c> attribute in this layer.
    /// </summary>
    internal const string LogicalName = "ownaudio_ffi";

    private static bool _registered;

    /// <summary>
    /// Registers the custom resolver exactly once. Idempotent — safe to call multiple times.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
        _registered = true;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LogicalName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        if (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS())
        {
            return NativeLibrary.GetMainProgramHandle();
        }

        if (OperatingSystem.IsAndroid())
        {
            return NativeLibrary.TryLoad("libownaudio_ffi.so", assembly, searchPath, out IntPtr androidHandle)
                ? androidHandle
                : IntPtr.Zero;
        }

        string fileName = GetPlatformFileName();
        string baseDir = AppContext.BaseDirectory;

        string ridPath = Path.Combine(baseDir, "runtimes", GetCurrentRid(), "native", fileName);
        if (NativeLibrary.TryLoad(ridPath, out IntPtr handle))
        {
            return handle;
        }

        string sideBySide = Path.Combine(baseDir, fileName);
        if (NativeLibrary.TryLoad(sideBySide, out handle))
        {
            return handle;
        }

        return NativeLibrary.TryLoad(fileName, assembly, searchPath, out handle) ? handle : IntPtr.Zero;
    }

    private static string GetPlatformFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "ownaudio_ffi.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "libownaudio_ffi.dylib";
        }

        return "libownaudio_ffi.so";
    }

    private static string GetCurrentRid()
    {
        string os;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            os = "win";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            os = "osx";
        else
            os = "linux";

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.X86   => "x86",
            Architecture.Arm   => "arm",
            Architecture.Arm64 => "arm64",
            _                  => "x64"
        };

        return $"{os}-{arch}";
    }
}
