namespace OwnaudioNET.Exceptions;

/// <summary>
/// Base type for every audio error we throw.
/// </summary>
public class AudioException : Exception
{
    /// <summary>
    /// Native/engine error code if we got one.
    /// </summary>
    public int? ErrorCode { get; }

    /// <summary>
    /// Empty ctor, nothing to say.
    /// </summary>
    public AudioException() { }

    /// <summary>
    /// Plain message only.
    /// </summary>
    /// <param name="message"></param>
    public AudioException(string message) : base(message) { }

    /// <summary>
    /// Message plus the code the engine gave back.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="errorCode"></param>
    public AudioException(string message, int errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Message and the thing that blew up under us.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="innerException"></param>
    public AudioException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>
    /// Everything at once, code and inner.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="errorCode"></param>
    /// <param name="innerException"></param>
    public AudioException(string message, int errorCode, Exception innerException) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Engine level trouble, mostly init or device setup.
/// </summary>
public class AudioEngineException : AudioException
{
    /// <summary>
    /// Empty ctor, nothing to say.
    /// </summary>
    public AudioEngineException() { }

    /// <summary>
    /// Plain message only.
    /// </summary>
    /// <param name="message"></param>
    public AudioEngineException(string message) : base(message) { }

    /// <summary>
    /// Message plus the code the engine gave back.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="errorCode"></param>
    public AudioEngineException(string message, int errorCode) : base(message, errorCode) { }

    /// <summary>
    /// Message and the thing that blew up under us.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="innerException"></param>
    public AudioEngineException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>
    /// Everything at once, code and inner.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="errorCode"></param>
    /// <param name="innerException"></param>
    public AudioEngineException(string message, int errorCode, Exception innerException) : base(message, errorCode, innerException) { }
}
