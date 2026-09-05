using System.Reflection;
using System.Runtime.InteropServices;
using OwnAudio.Shared;

namespace OwnAudio.Midi.Interop;

/// <summary>
/// Finds the ownaudio_midi_ffi native lib on every platform. Has to be hooked up
/// before the first P/Invoke — MidiNativeMethods' cctor does that for us.
/// </summary>
internal static class MidiNativeLibraryLoader
{
    /// <summary>
    /// The name every [LibraryImport] here uses.
    /// </summary>
    internal const string LogicalName = "ownaudio_midi_ffi";

    /// <summary>
    /// So we only hook the resolver once.
    /// </summary>
    private static bool _registered;

    /// <summary>
    /// Hooks up the resolver, idempotent.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;

        NativeLibrary.SetDllImportResolver(typeof(MidiNativeLibraryLoader).Assembly, _resolve);
        _registered = true;
    }

    /// <summary>
    /// RID-specific runtimes folder first, then next to the exe, then whatever the
    /// OS search path turns up. Zero means we gave up.
    /// </summary>
    private static IntPtr _resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LogicalName, StringComparison.Ordinal)) return IntPtr.Zero;

        //iOS links the midi ffi statically, so the symbols are already in the main image
        if (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS())
            return NativeLibrary.GetMainProgramHandle();

        return NativeLibResolver.Resolve("ownaudio_midi_ffi", assembly, searchPath);
    }
}
