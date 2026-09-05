using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OwnAudio.Shared;

/// <summary>
/// The part of native library resolution that is the same for every ffi we ship: rid folder
/// first, then next to the exe, then let the OS look. Source-linked into OwnAudioRust,
/// OwnAudio.Midi and OwnaudioNET.Mt3 — they have no assembly in common, and having the rid
/// table in one place is the point (Mt3's copy had already lost the x86 and arm cases).
/// Platform quirks that differ per library — iOS static linking, the Android JNI bootstrap —
/// stay with the individual loaders.
/// </summary>
internal static class NativeLibResolver
{
    /// <summary>
    /// Looks for lib named by stem ("ownaudio_ffi") and hands back its handle, zero if we gave up.
    /// Android loads by soname straight away, the app package has no runtimes folder.
    /// </summary>
    /// <param name="stem">library name without the lib prefix or the extension</param>
    internal static IntPtr Resolve(string stem, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (OperatingSystem.IsAndroid())
            return NativeLibrary.TryLoad($"lib{stem}.so", assembly, searchPath, out IntPtr _droid) ? _droid : IntPtr.Zero;

        string _fileName = FileName(stem);
        string _baseDir = AppContext.BaseDirectory;

        if (NativeLibrary.TryLoad(Path.Combine(_baseDir, "runtimes", CurrentRid(), "native", _fileName), out IntPtr _handle))
            return _handle;

        if (NativeLibrary.TryLoad(Path.Combine(_baseDir, _fileName), out _handle))
            return _handle;

        return NativeLibrary.TryLoad(_fileName, assembly, searchPath, out _handle) ? _handle : IntPtr.Zero;
    }

    /// <summary>
    /// dll / dylib / so depending on where we run.
    /// </summary>
    internal static string FileName(string stem)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"{stem}.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"lib{stem}.dylib";

        return $"lib{stem}.so";
    }

    /// <summary>
    /// Runtime id for the current OS + process arch, like win-x64 or osx-arm64.
    /// </summary>
    internal static string CurrentRid()
    {
        string _os = "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) _os = "win";
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
