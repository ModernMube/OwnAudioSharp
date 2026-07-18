using System;

namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Base for everything the native engine throws at us. Carries the error code so
/// callers don't have to parse messages.
/// </summary>
public class OwnAudioException : Exception
{
    /// <summary>
    /// Error code the FFI layer gave back.
    /// </summary>
    public AudioEngineErrorCode ErrorCode { get; }

    /// <param name="errorCode"></param>
    /// <param name="message"></param>
    public OwnAudioException(AudioEngineErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Same, but wraps whatever blew up underneath.
    /// </summary>
    public OwnAudioException(AudioEngineErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
