using System.Reflection;
using System.Runtime.InteropServices;

namespace OwnaudioNET.Features.Extensions.Mt3Interop;

/// <summary>
/// Finds ownaudio_mt3_ffi next to the app. Desktop only — MT3 drags ONNX Runtime along and
/// chord detection isn't compiled into the mobile package anyway.
/// </summary>
internal static class Mt3NativeLibraryLoader
{
    /// <summary>
    /// The name every [LibraryImport] here uses.
    /// </summary>
    internal const string LogicalName = "ownaudio_mt3_ffi";

    private static bool _registered;

    /// <summary>
    /// Hooks up the resolver, idempotent.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;

        NativeLibrary.SetDllImportResolver(typeof(Mt3NativeLibraryLoader).Assembly, _resolve);
        _registered = true;
    }

    /// <summary>
    /// RID-specific runtimes folder first, then next to the exe, then the OS search path.
    /// </summary>
    private static IntPtr _resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LogicalName, StringComparison.Ordinal)) return IntPtr.Zero;

        string _fileName = _platformFileName();
        string _baseDir = AppContext.BaseDirectory;

        if (NativeLibrary.TryLoad(Path.Combine(_baseDir, "runtimes", _currentRid(), "native", _fileName), out IntPtr handle))
            return handle;

        if (NativeLibrary.TryLoad(Path.Combine(_baseDir, _fileName), out handle))
            return handle;

        return NativeLibrary.TryLoad(_fileName, assembly, searchPath, out handle) ? handle : IntPtr.Zero;
    }

    private static string _platformFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "ownaudio_mt3_ffi.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libownaudio_mt3_ffi.dylib";
        return "libownaudio_mt3_ffi.so";
    }

    /// <summary>
    /// Runtime id for the current OS + process arch, like win-x64.
    /// </summary>
    private static string _currentRid()
    {
        string _os;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _os = "win";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            _os = "osx";
        else
            _os = "linux";

        string _arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        return $"{_os}-{_arch}";
    }
}
