using System.Reflection;
using System.Runtime.InteropServices;

namespace OwnAudio.Midi.Interop;

/// <summary>
/// Resolves the native <c>ownaudio_midi_ffi</c> library across platforms using
/// <see cref="NativeLibrary.SetDllImportResolver"/>. Must be registered once
/// before any <c>LibraryImport</c> call is made; the static constructor of
/// <see cref="MidiNativeMethods"/> calls <see cref="EnsureRegistered"/>.
/// </summary>
internal static class MidiNativeLibraryLoader
{
    /// <summary>
    /// The logical library name used in every <c>[LibraryImport]</c> attribute in this layer.
    /// </summary>
    internal const string LogicalName = "ownaudio_midi_ffi";

    /// <summary>
    /// Guards against registering the resolver more than once.
    /// </summary>
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

        NativeLibrary.SetDllImportResolver(typeof(MidiNativeLibraryLoader).Assembly, Resolve);
        _registered = true;
    }

    /// <summary>
    /// Attempts to load the native library, preferring the RID-specific
    /// <c>runtimes/&lt;rid&gt;/native</c> path and falling back to the application
    /// base directory and the default OS search path.
    /// </summary>
    /// <param name="libraryName">
    /// The logical name requested by the P/Invoke marshaller.
    /// </param>
    /// <param name="assembly">
    /// The assembly that declared the import.
    /// </param>
    /// <param name="searchPath">
    /// The configured default search path, if any.
    /// </param>
    /// <returns>
    /// A handle to the loaded library, or <see cref="IntPtr.Zero"/> if it could not be found.
    /// </returns>
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
            return NativeLibrary.TryLoad("libownaudio_midi_ffi.so", assembly, searchPath, out IntPtr androidHandle)
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

    /// <summary>
    /// Returns the platform-specific native library file name.
    /// </summary>
    private static string GetPlatformFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "ownaudio_midi_ffi.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "libownaudio_midi_ffi.dylib";
        }

        return "libownaudio_midi_ffi.so";
    }

    /// <summary>
    /// Returns the runtime identifier (for example <c>win-x64</c>) for the current
    /// operating system and process architecture.
    /// </summary>
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
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        return $"{os}-{arch}";
    }
}
