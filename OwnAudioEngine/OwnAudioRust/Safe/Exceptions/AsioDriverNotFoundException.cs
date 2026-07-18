namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// ASIO is compiled into the native binary, but the machine has no ASIO driver installed
/// (ASIO4ALL, RME, Focusrite, whatever). Either install one or just let the platform default win.
/// </summary>
public sealed class AsioDriverNotFoundException : OwnAudioException
{
    /// <param name="message"></param>
    public AsioDriverNotFoundException(string message)
        : base(AudioEngineErrorCode.AsioDriverNotFound, message) { }
}
