using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using OwnAudio.Shared;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Finds the ownaudio_ffi native lib on every platform we support.
/// Has to be registered before the first LibraryImport call, OwnAudioNative does it in its cctor.
/// </summary>
internal static class NativeLibraryLoader
{
    /// <summary>
    /// The name every [LibraryImport] in this layer uses.
    /// </summary>
    /// <remarks>
    /// On iOS the engine is a static .a linked straight into the app, and "__Internal" is how you
    /// say that: the AOT compiler then emits a direct reference to each symbol. Going through a
    /// resolver and dlsym instead does not work there — nothing references the Rust symbols
    /// statically, so -dead_strip throws all of them out during the native link and the lookup
    /// finds an empty binary.
    /// </remarks>
#if IOS || TVOS
    internal const string LogicalName = "__Internal";
#else
    internal const string LogicalName = "ownaudio_ffi";
#endif

    private static bool _registered;

    /// <summary>
    /// Hooks up our resolver, only once. Calling it again does nothing.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered) { return; }
        _registered = true;

        //Statically linked, the runtime resolves __Internal itself — a resolver would only get in the way
        if (!OperatingSystem.IsIOS() && !OperatingSystem.IsTvOS())
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, _resolve);

        AndroidJniBootstrap.EnsureInitialized();
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

        return NativeLibResolver.Resolve("ownaudio_ffi", assembly, searchPath);
    }
}
