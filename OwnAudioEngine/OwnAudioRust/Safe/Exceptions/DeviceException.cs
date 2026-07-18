namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Device op failed — requested device is gone, or the OS audio subsystem choked while enumerating.
/// </summary>
public sealed class DeviceException : OwnAudioException
{
    /// <param name="errorCode"></param>
    /// <param name="message"></param>
    public DeviceException(AudioEngineErrorCode errorCode, string message)
        : base(errorCode, message) { }
}
