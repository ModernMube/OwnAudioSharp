namespace OwnaudioNET.Exceptions;

/// <summary>
/// Base exception for all audio-related errors in OwnaudioNET.
/// </summary>
public class AudioException : Exception
{
    /// <summary>
    /// Gets the error code associated with this exception, if available.
    /// </summary>
    public int? ErrorCode { get; }

    /// <summary>
    /// Initializes a new instance of the AudioException class.
    /// </summary>
    public AudioException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the AudioException class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public AudioException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the AudioException class with a specified error message and error code.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="errorCode">The error code associated with this exception.</param>
    public AudioException(string message, int errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new instance of the AudioException class with a specified error message and a reference to the inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public AudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the AudioException class with a specified error message, error code, and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="errorCode">The error code associated with this exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public AudioException(string message, int errorCode, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Exception thrown when an audio engine initialization fails.
/// </summary>
public class AudioEngineException : AudioException
{
    /// <summary>
    /// Initializes a new instance of the AudioEngineException class.
    /// </summary>
    public AudioEngineException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the AudioEngineException class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public AudioEngineException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the AudioEngineException class with a specified error message and error code.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="errorCode">The error code associated with this exception.</param>
    public AudioEngineException(string message, int errorCode)
        : base(message, errorCode)
    {
    }

    /// <summary>
    /// Initializes a new instance of the AudioEngineException class with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public AudioEngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the AudioEngineException class with a specified error message, error code, and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="errorCode">The error code associated with this exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public AudioEngineException(string message, int errorCode, Exception innerException)
        : base(message, errorCode, innerException)
    {
    }
}
