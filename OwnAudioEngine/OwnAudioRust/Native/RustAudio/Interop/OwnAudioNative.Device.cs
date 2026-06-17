using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Structs;

namespace Ownaudio.Native.RustAudio.Interop;

internal static unsafe partial class OwnAudioNative
{
    #region Device enumeration

    /// <summary>
    /// Lists all available output devices on the default host.
    /// </summary>
    /// <param name="outDevices">
    /// On success, receives a pointer to a Rust-owned array of
    /// <see cref="NativeDeviceInfo"/> elements.  Must be released with
    /// <see cref="ownaudio_v1_free_device_list"/>.
    /// </param>
    /// <param name="outCount">On success, receives the number of elements in the array.</param>
    /// <returns>
    /// <see cref="NativeErrorCode.Success"/> (0) on success;
    /// a non-zero <see cref="NativeErrorCode"/> otherwise.
    /// </returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_list_output_devices(OwnAudioDeviceInfo**, size_t*) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_list_output_devices(
        out IntPtr outDevices,
        out nuint outCount);

    /// <summary>
    /// Lists all available input devices on the default host.
    /// </summary>
    /// <param name="outDevices">
    /// On success, receives a pointer to a Rust-owned array of
    /// <see cref="NativeDeviceInfo"/> elements.  Must be released with
    /// <see cref="ownaudio_v1_free_device_list"/>.
    /// </param>
    /// <param name="outCount">On success, receives the number of elements in the array.</param>
    /// <returns>
    /// <see cref="NativeErrorCode.Success"/> (0) on success;
    /// a non-zero <see cref="NativeErrorCode"/> otherwise.
    /// </returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_list_input_devices(OwnAudioDeviceInfo**, size_t*) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_list_input_devices(
        out IntPtr outDevices,
        out nuint outCount);

    /// <summary>
    /// Releases a device array previously returned by
    /// <see cref="ownaudio_v1_list_output_devices"/> or
    /// <see cref="ownaudio_v1_list_input_devices"/>.
    /// </summary>
    /// <param name="devices">Pointer to the first element of the array.  Null is safe.</param>
    /// <param name="count">Number of elements.  Zero is safe.</param>
    /// <remarks>Mirrors: <c>ownaudio_v1_free_device_list(OwnAudioDeviceInfo*, size_t) → void</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_free_device_list(IntPtr devices, nuint count);

    #endregion
}
