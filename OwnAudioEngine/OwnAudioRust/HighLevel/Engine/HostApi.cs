namespace Ownaudio.Audio;

/// <summary>
/// Picks the native audio backend for <see cref="Safe.AudioEngine.Create(HostApi?)"/>.
/// The numbers are C ABI, keep them in sync with ownaudio_ffi.h and NativeHostApi.
/// Pass null instead and you get the platform default, which is what you want
/// almost every time.
/// </summary>
public enum HostApi
{
    /// <summary>Windows Audio Session API, the default on Windows.</summary>
    Wasapi = 0,

    /// <summary>
    /// Steinberg ASIO. Needs an --features asio build plus a driver on the box,
    /// otherwise you get back HostApiNotAvailable.
    /// </summary>
    Asio = 1,

    /// <summary>Apple Core Audio, the default on macOS and on iOS too.</summary>
    CoreAudio = 2,

    /// <summary>ALSA, the default on Linux.</summary>
    Alsa = 3,

    /// <summary>Android AAudio, the fast path from Android 8.0 up.</summary>
    AAudio = 4,
}
