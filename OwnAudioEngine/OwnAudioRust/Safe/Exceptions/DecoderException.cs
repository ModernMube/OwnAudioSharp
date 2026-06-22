namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Thrown when a streaming decode operation fails — for example, when a file
/// cannot be opened, its format is unsupported, or decoding/seeking fails.
/// </summary>
public sealed class DecoderException : OwnAudioException
{
    #region Construction

    /// <summary>
    /// Initializes a new instance of <see cref="DecoderException"/> with the given
    /// error code and a descriptive message.
    /// </summary>
    /// <param name="errorCode">The error code identifying the failure category.</param>
    /// <param name="message">Human-readable description of the failure.</param>
    public DecoderException(AudioEngineErrorCode errorCode, string message)
        : base(errorCode, message)
    {
    }

    #endregion
}
