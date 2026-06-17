using System;

namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Base exception type for all OwnAudioSharp errors originating from the native audio engine.
/// Carry the <see cref="AudioEngineErrorCode"/> so callers can distinguish error categories
/// without string parsing.
/// </summary>
public class OwnAudioException : Exception
{
    #region Properties

    /// <summary>
    /// The error code returned by the native FFI layer that caused this exception.
    /// </summary>
    public AudioEngineErrorCode ErrorCode { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new instance of <see cref="OwnAudioException"/> with the given
    /// error code and a descriptive message.
    /// </summary>
    /// <param name="errorCode">The error code identifying the failure category.</param>
    /// <param name="message">Human-readable description of the failure.</param>
    public OwnAudioException(AudioEngineErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="OwnAudioException"/> with the given
    /// error code, a descriptive message, and an inner exception.
    /// </summary>
    /// <param name="errorCode">The error code identifying the failure category.</param>
    /// <param name="message">Human-readable description of the failure.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public OwnAudioException(AudioEngineErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    #endregion
}
