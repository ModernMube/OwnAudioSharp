using System.Reflection;
using System.Runtime.InteropServices;

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

        if (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS())
            return NativeLibrary.GetMainProgramHandle();

        if (OperatingSystem.IsAndroid())
        {
            return NativeLibrary.TryLoad("libownaudio_midi_ffi.so", assembly, searchPath, out IntPtr _android)
                ? _android : IntPtr.Zero;
        }

        string _fileName = _platformFileName();
        string _baseDir = AppContext.BaseDirectory;

        if (NativeLibrary.TryLoad(Path.Combine(_baseDir, "runtimes", _currentRid(), "native", _fileName), out IntPtr handle))
            return handle;

        if (NativeLibrary.TryLoad(Path.Combine(_baseDir, _fileName), out handle))
            return handle;

        return NativeLibrary.TryLoad(_fileName, assembly, searchPath, out handle) ? handle : IntPtr.Zero;
    }

    /// <summary>
    /// dll / dylib / so depending on where we run.
    /// </summary>
    private static string _platformFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "ownaudio_midi_ffi.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libownaudio_midi_ffi.dylib";
        return "libownaudio_midi_ffi.so";
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
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        return $"{_os}-{_arch}";
    }
}
