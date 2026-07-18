using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Structs;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Device enumeration P/Invokes. The lists come back rust-owned, we hand them back with free_device_list.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    #region Device enumeration

    /// <summary>
    /// Output devices of the default host. outDevices gets a rust-owned NativeDeviceInfo array.
    /// </summary>
    /// <param name="outDevices"></param>
    /// <param name="outCount"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_list_output_devices(
        out IntPtr outDevices,
        out nuint outCount);

    /// <summary>
    /// Same as above for the capture side.
    /// </summary>
    /// <param name="outDevices"></param>
    /// <param name="outCount"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_list_input_devices(
        out IntPtr outDevices,
        out nuint outCount);

    /// <summary>
    /// Output devices of the engine's own host, not the platform default one.
    /// So an ASIO engine lists ASIO stuff and not wasapi endpoints.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="outDevices"></param>
    /// <param name="outCount"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_engine_list_output_devices(
        IntPtr engine,
        out IntPtr outDevices,
        out nuint outCount);

    /// <summary>
    /// Input side of the engine scoped listing.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="outDevices"></param>
    /// <param name="outCount"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_engine_list_input_devices(
        IntPtr engine,
        out IntPtr outDevices,
        out nuint outCount);

    /// <summary>
    /// Gives back a device array we got from any of the list calls. Null pointer and zero count are both fine.
    /// </summary>
    /// <param name="devices">first element of the array</param>
    /// <param name="count"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_free_device_list(IntPtr devices, nuint count);

    #endregion
}
