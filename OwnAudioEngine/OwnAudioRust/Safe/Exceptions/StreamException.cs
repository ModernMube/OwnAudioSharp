namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Stream lifecycle failed — couldn't build it with the given config, or start/stop bailed.
/// </summary>
public sealed class StreamException : OwnAudioException
{
    /// <param name="errorCode"></param>
    /// <param name="message"></param>
    public StreamException(AudioEngineErrorCode errorCode, string message)
        : base(errorCode, message) { }
}
