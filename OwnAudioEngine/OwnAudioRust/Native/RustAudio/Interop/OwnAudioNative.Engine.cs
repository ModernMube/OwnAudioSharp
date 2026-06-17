using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Source-generated <c>LibraryImport</c> P/Invoke declarations for the
/// <c>ownaudio_ffi</c> native library.
/// This is an internal, thin 1:1 mirror of the C ABI — it contains no business logic,
/// no error handling, and no public surface. All types are internal.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    static OwnAudioNative()
    {
        NativeLibraryLoader.EnsureRegistered();
    }

    #region Diagnostics

    /// <summary>
    /// Returns a pointer to the last error message string recorded on this thread,
    /// or <see cref="IntPtr.Zero"/> when no error is set.
    /// </summary>
    /// <remarks>
    /// The pointer is valid until the next FFI call on this thread that records an error,
    /// or until the thread exits.  The caller must <b>not</b> free it.
    /// Read the string with <c>Marshal.PtrToStringUTF8</c>.
    /// Mirrors: <c>ownaudio_v1_last_error_message(void) → const char*</c>
    /// </remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial IntPtr ownaudio_v1_last_error_message();

    #endregion

    #region Engine lifecycle

    /// <summary>
    /// Creates a new audio engine and writes its opaque handle to <paramref name="outHandle"/>.
    /// </summary>
    /// <param name="outHandle">
    /// Receives the new engine handle on success.  Must be released with
    /// <see cref="ownaudio_v1_engine_destroy"/>.
    /// </param>
    /// <returns>
    /// <see cref="NativeErrorCode.Success"/> (0) on success;
    /// a non-zero <see cref="NativeErrorCode"/> otherwise.
    /// </returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_engine_create(OwnAudioEngineHandle**) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_engine_create(out IntPtr outHandle);

    /// <summary>
    /// Destroys an engine handle created by <see cref="ownaudio_v1_engine_create"/>.
    /// </summary>
    /// <param name="handle">
    /// The handle to destroy.  All streams opened from this engine must be
    /// destroyed before this call.  Passing <see cref="IntPtr.Zero"/> is safe.
    /// </param>
    /// <remarks>Mirrors: <c>ownaudio_v1_engine_destroy(OwnAudioEngineHandle*) → void</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_engine_destroy(IntPtr handle);

    #endregion
}
