using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// LibraryImport declarations for ownaudio_ffi. Thin 1:1 mirror of the C ABI, no logic in here,
/// everything internal. The safe layer does the error mapping.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    static OwnAudioNative()
    {
        NativeLibraryLoader.EnsureRegistered();
    }

    #region Diagnostics

    /// <summary>
    /// Last error message recorded on this thread, zero when there was none.
    /// Valid until the next failing call on the same thread, don't free it. Marshal.PtrToStringUTF8 reads it.
    /// </summary>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial IntPtr ownaudio_v1_last_error_message();

    #endregion

    #region Engine lifecycle

    /// <summary>
    /// Spins up an engine, handle lands in outHandle. Release it with engine_destroy.
    /// </summary>
    /// <param name="outHandle"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_engine_create(out IntPtr outHandle);

    /// <summary>
    /// Tears the engine down. Every stream opened from it must be gone already, zero handle is a no-op.
    /// </summary>
    /// <param name="handle"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_engine_destroy(IntPtr handle);

    #endregion
}
