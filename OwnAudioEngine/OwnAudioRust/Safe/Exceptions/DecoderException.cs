namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Decode side blew up — file won't open, format unsupported, read or seek failed.
/// </summary>
public sealed class DecoderException : OwnAudioException
{
    /// <param name="errorCode"></param>
    /// <param name="message"></param>
    public DecoderException(AudioEngineErrorCode errorCode, string message)
        : base(errorCode, message) { }
}
