namespace Ownaudio.Audio;

/// <summary>
/// Selects the native audio host API used by <see cref="Safe.AudioEngine.Create(HostApi?)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Numeric values are part of the stable C ABI and must stay in sync with
/// <c>ownaudio_ffi.h</c> and <c>Ownaudio.Native.RustAudio.Interop.NativeHostApi</c>.
/// </para>
/// <para>
/// Pass <see langword="null"/> to <see cref="Safe.AudioEngine.Create(HostApi?)"/> to use
/// the platform default (WASAPI on Windows, Core Audio on macOS, ALSA on Linux).
/// </para>
/// </remarks>
public enum HostApi
{
    /// <summary>Windows Audio Session API — the default Windows backend.</summary>
    Wasapi = 0,

    /// <summary>Steinberg ASIO — requires an installed ASIO driver.</summary>
    Asio = 1,

    /// <summary>Apple Core Audio — the default macOS backend.</summary>
    CoreAudio = 2,

    /// <summary>Advanced Linux Sound Architecture — the default Linux backend.</summary>
    Alsa = 3,
}
