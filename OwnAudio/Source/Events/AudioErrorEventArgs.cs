namespace OwnaudioNET.Events;

/// <summary>
/// Event arguments for audio error events.
/// </summary>
public sealed class AudioErrorEventArgs : EventArgs
{
    /// <summary>
    /// Gets the error message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the exception that caused the error, if available.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Gets the timestamp when the error occurred.
    /// </summary>
    public DateTime Timestamp { get; }

    public AudioErrorEventArgs(string message, Exception? exception = null)
    {
        Message = message;
        Exception = exception;
        Timestamp = DateTime.UtcNow;
    }

    public override string ToString()
    {
        return Exception != null
            ? $"Audio error at {Timestamp:HH:mm:ss.fff}: {Message} - {Exception.Message}"
            : $"Audio error at {Timestamp:HH:mm:ss.fff}: {Message}";
    }
}
