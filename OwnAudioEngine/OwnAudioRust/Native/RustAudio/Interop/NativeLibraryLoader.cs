using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Finds the ownaudio_ffi native lib on every platform we support.
/// Has to be registered before the first LibraryImport call, OwnAudioNative does it in its cctor.
/// </summary>
internal static class NativeLibraryLoader
{
    /// The name every [LibraryImport] in this layer uses.
    internal const string LogicalName = "ownaudio_ffi";

    private static bool _registered;

    /// <summary>
    /// Hooks up our resolver, only once. Calling it again does nothing.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered) { return; }

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, _resolve);
        _registered = true;
    }

    /// <summary>
    /// The resolver itself. Rid folder first, then next to the exe, then let the loader search.
    /// </summary>
    /// <param name="libraryName"></param>
    /// <param name="assembly"></param>
    /// <param name="searchPath"></param>
    private static IntPtr _resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LogicalName, StringComparison.Ordinal))
            return IntPtr.Zero;

        //On ios everything is linked into the main binary
        if (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS())
            return NativeLibrary.GetMainProgramHandle();

        if (OperatingSystem.IsAndroid())
            return NativeLibrary.TryLoad("libownaudio_ffi.so", assembly, searchPath, out IntPtr _droidHandle) ? _droidHandle : IntPtr.Zero;

        string _fileName = _getPlatformFileName();
        string _baseDir = AppContext.BaseDirectory;

        string _ridPath = Path.Combine(_baseDir, "runtimes", _getCurrentRid(), "native", _fileName);
        if (NativeLibrary.TryLoad(_ridPath, out IntPtr _handle)) return _handle;

        if (NativeLibrary.TryLoad(Path.Combine(_baseDir, _fileName), out _handle)) return _handle;

        return NativeLibrary.TryLoad(_fileName, assembly, searchPath, out _handle) ? _handle : IntPtr.Zero;
    }

    /// <summary>
    /// The lib file name for the current os.
    /// </summary>
    /// <returns></returns>
    private static string _getPlatformFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "ownaudio_ffi.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libownaudio_ffi.dylib";

        return "libownaudio_ffi.so";
    }

    /// <summary>
    /// Builds the rid string we look for under runtimes, like win-x64 or osx-arm64.
    /// </summary>
    private static string _getCurrentRid()
    {
        string _os = "linux";
        if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) _os = "win";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) _os = "osx";

        string _arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        return $"{_os}-{_arch}";
    }
}
