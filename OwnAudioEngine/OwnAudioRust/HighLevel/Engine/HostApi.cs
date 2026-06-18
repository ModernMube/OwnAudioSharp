namespace Ownaudio.Audio;

/// <summary>
/// Identifies the OS audio host API to use when creating an <see cref="AudioEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pass a value via <see cref="AudioEngineOptions.PreferredHostApi"/> to select a
/// specific backend instead of the platform default.  Leave the property
/// <see langword="null"/> to use the default for the current OS (WASAPI on Windows,
/// Core Audio on macOS, ALSA on Linux).
/// </para>
/// <para>
/// Not all variants are available on every platform or binary build:
/// <list type="bullet">
///   <item><see cref="Asio"/> requires Windows and a native binary built with <c>--features asio</c>.</item>
///   <item><see cref="CoreAudio"/> is only meaningful on macOS.</item>
///   <item><see cref="Alsa"/> is only meaningful on Linux.</item>
/// </list>
/// Requesting an unavailable variant causes <see cref="AudioEngine.Create"/> to throw
/// <see cref="Safe.Exceptions.HostApiNotAvailableException"/> or
/// <see cref="Safe.Exceptions.AsioDriverNotFoundException"/>.
/// </para>
/// </remarks>
public enum HostApi
{
    /// <summary>Windows Audio Session API — the default Windows audio backend.</summary>
    Wasapi = 0,

    /// <summary>
    /// Steinberg ASIO — low-latency Windows audio for professional audio interfaces.
    /// Requires the native binary to be built with <c>--features asio</c> and
    /// an installed ASIO driver (e.g. ASIO4ALL, RME, Focusrite).
    /// </summary>
    Asio = 1,

    /// <summary>Apple Core Audio — the default macOS audio backend.</summary>
    CoreAudio = 2,

    /// <summary>Advanced Linux Sound Architecture — the default Linux audio backend.</summary>
    Alsa = 3,
}
