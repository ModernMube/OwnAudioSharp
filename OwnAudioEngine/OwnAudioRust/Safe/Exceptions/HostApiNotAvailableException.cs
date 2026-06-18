namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Thrown when the requested audio host API is not available on this machine.
/// </summary>
/// <remarks>
/// <para>
/// This exception is raised when <see cref="AudioEngine.Create(Ownaudio.Audio.HostApi?)"/> is called
/// with a <see cref="Ownaudio.Audio.HostApi"/> that is either not compiled into the native
/// binary (e.g. ASIO without the <c>asio</c> Cargo feature), or not available on
/// the current platform (e.g. requesting WASAPI on macOS).
/// </para>
/// <para>
/// To resolve: verify that the native <c>ownaudio_ffi</c> binary was built with the
/// required feature flag, and that the host API is supported on the current OS.
/// For ASIO specifically, ensure an ASIO driver is installed — if no driver is found
/// at runtime, <see cref="AsioDriverNotFoundException"/> is thrown instead.
/// </para>
/// </remarks>
public sealed class HostApiNotAvailableException : OwnAudioException
{
    #region Construction

    /// <summary>
    /// Initializes a new instance of <see cref="HostApiNotAvailableException"/>.
    /// </summary>
    /// <param name="message">Human-readable description of the failure.</param>
    public HostApiNotAvailableException(string message)
        : base(AudioEngineErrorCode.HostApiNotAvailable, message)
    {
    }

    #endregion
}
