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
    /// <remarks>
    /// iOS links the MIDI exports statically (inside OwnAudioSharp.Mobile's libownaudio_ffi.a, or
    /// this package's own archive), and "__Internal" makes the AOT compiler reference each symbol
    /// directly. A resolver handing back the main image looked fine but referenced nothing, so
    /// -dead_strip took every MIDI export out of the app and the first call hit
    /// EntryPointNotFoundException. Same deal as the audio engine's NativeLibraryLoader.
    /// </remarks>
#if IOS || TVOS
    internal const string LogicalName = "__Internal";
#else
    internal const string LogicalName = "ownaudio_midi_ffi";
#endif

    /// <summary>
    /// So we only hook the resolver once.
    /// </summary>
    private static bool _registered;

    /// <summary>
    /// Hooks up the resolver, idempotent. Not on iOS: the runtime resolves __Internal itself.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;

        if (!OperatingSystem.IsIOS() && !OperatingSystem.IsTvOS())
            NativeLibrary.SetDllImportResolver(typeof(MidiNativeLibraryLoader).Assembly, _resolve);
    }

    /// <summary>
    /// RID-specific runtimes folder first, then next to the exe, then whatever the
    /// OS search path turns up. Zero means we gave up.
    /// </summary>
    private static IntPtr _resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LogicalName, StringComparison.Ordinal)) return IntPtr.Zero;

        return NativeLibResolver.Resolve("ownaudio_midi_ffi", assembly, searchPath);
    }
}
