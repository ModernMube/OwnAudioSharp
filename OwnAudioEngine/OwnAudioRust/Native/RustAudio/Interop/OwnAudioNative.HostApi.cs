using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// OwnHostApi from ownaudio_ffi.h, cbindgen generated. Values must match one to one.
/// </summary>
internal enum NativeHostApi : int
{
    /// <summary>Windows Audio Session API, the default on windows.</summary>
    Wasapi = 0,

    /// <summary>Steinberg ASIO, needs --features asio plus an installed driver.</summary>
    Asio = 1,

    /// <summary>Apple Core Audio, default on macOS and iOS.</summary>
    CoreAudio = 2,

    /// <summary>ALSA, default on linux.</summary>
    Alsa = 3,

    /// <summary>Android AAudio, default from 8.0 up.</summary>
    AAudio = 4,
}

/// <summary>
/// Engine creation when we want to pick the host api ourselves.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    #region Host API engine creation

    /// <summary>
    /// Engine on an explicitly picked host api. Comes back with HostApiNotAvailable (10) when it isn't
    /// compiled in or the platform doesn't have it, and AsioDriverNotFound (11) when asio is in but no driver is.
    /// </summary>
    /// <param name="hostApi"></param>
    /// <param name="outHandle"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_engine_create_with_host(
        NativeHostApi hostApi,
        out IntPtr outHandle);

    #endregion
}
