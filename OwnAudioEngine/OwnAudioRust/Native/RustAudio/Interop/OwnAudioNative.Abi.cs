using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// P/Invoke declarations for the native ABI version and package version query functions.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    #region ABI version

    /// <summary>
    /// Returns the ABI version baked into the native binary at compile time.
    /// The managed layer calls this immediately after loading the library and
    /// throws <see cref="Ownaudio.Safe.Exceptions.AbiVersionMismatchException"/>
    /// when the value does not equal <see cref="Ownaudio.Safe.AudioEngine.ExpectedAbiVersion"/>.
    /// </summary>
    /// <returns>
    /// The ABI version integer — currently <c>1</c> for the initial v1 surface.
    /// </returns>
    /// <remarks>
    /// Mirrors: <c>ownaudio_v1_get_abi_version(void) → u32</c>
    /// </remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial uint ownaudio_v1_get_abi_version();

    /// <summary>
    /// Returns a pointer to the null-terminated UTF-8 package version string
    /// baked into the binary at compile time (e.g. <c>"1.0.0"</c>).
    /// </summary>
    /// <returns>
    /// Pointer to a read-only, process-lifetime string.  Do <b>not</b> free it.
    /// Read with <see cref="System.Runtime.InteropServices.Marshal.PtrToStringUTF8(System.IntPtr)"/>.
    /// </returns>
    /// <remarks>
    /// Mirrors: <c>ownaudio_v1_get_package_version(void) → const char*</c>
    /// </remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial System.IntPtr ownaudio_v1_get_package_version();

    #endregion
}
