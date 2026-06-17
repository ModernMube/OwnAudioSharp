namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Thrown when a stream lifecycle operation fails — for example, when a stream cannot be built
/// with the supplied configuration, or starting/stopping the stream fails.
/// </summary>
public sealed class StreamException : OwnAudioException
{
    #region Construction

    /// <summary>
    /// Initializes a new instance of <see cref="StreamException"/> with the given
    /// error code and a descriptive message.
    /// </summary>
    /// <param name="errorCode">The error code identifying the failure category.</param>
    /// <param name="message">Human-readable description of the failure.</param>
    public StreamException(AudioEngineErrorCode errorCode, string message)
        : base(errorCode, message)
    {
    }

    #endregion
}
