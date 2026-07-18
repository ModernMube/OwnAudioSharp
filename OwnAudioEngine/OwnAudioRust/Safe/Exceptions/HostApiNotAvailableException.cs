namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Requested host API isn't usable here — either not compiled into the native binary
/// (ASIO without the cargo feature), or plain wrong for the OS (WASAPI on macOS).
/// If ASIO is there but driverless you get <see cref="AsioDriverNotFoundException"/> instead.
/// </summary>
public sealed class HostApiNotAvailableException : OwnAudioException
{
    /// <param name="message"></param>
    public HostApiNotAvailableException(string message)
        : base(AudioEngineErrorCode.HostApiNotAvailable, message) { }
}
