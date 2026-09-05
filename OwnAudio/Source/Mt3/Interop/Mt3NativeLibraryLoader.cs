using System.Reflection;
using System.Runtime.InteropServices;
using OwnAudio.Shared;

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

        return NativeLibResolver.Resolve("ownaudio_mt3_ffi", assembly, searchPath);
    }
}
