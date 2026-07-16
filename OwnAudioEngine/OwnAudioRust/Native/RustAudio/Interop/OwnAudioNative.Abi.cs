using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Version queries on the native side, abi and package version.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    #region ABI version

    /// <summary>
    /// Abi version compiled into the native binary. We check it right after load and throw if it isn't what we expect.
    /// </summary>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial uint ownaudio_v1_get_abi_version();

    /// <summary>
    /// Pointer to the utf8 package version string, like "1.0.0". Lives as long as the process, never free it.
    /// Marshal.PtrToStringUTF8 reads it.
    /// </summary>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial System.IntPtr ownaudio_v1_get_package_version();

    #endregion
}
