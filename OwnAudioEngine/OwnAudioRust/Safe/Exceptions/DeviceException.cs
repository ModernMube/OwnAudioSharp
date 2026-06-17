namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Thrown when an audio device operation fails — for example, when a requested device
/// cannot be found or the OS audio subsystem fails during enumeration.
/// </summary>
public sealed class DeviceException : OwnAudioException
{
    #region Construction

    /// <summary>
    /// Initializes a new instance of <see cref="DeviceException"/> with the given
    /// error code and a descriptive message.
    /// </summary>
    /// <param name="errorCode">The error code identifying the failure category.</param>
    /// <param name="message">Human-readable description of the failure.</param>
    public DeviceException(AudioEngineErrorCode errorCode, string message)
        : base(errorCode, message)
    {
    }

    #endregion
}
