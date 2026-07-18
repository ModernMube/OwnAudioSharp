namespace OwnaudioNET.Events;

/// <summary>
/// What we hand out when something went wrong in the audio path.
/// </summary>
public sealed class AudioErrorEventArgs : EventArgs
{
    /// <summary>
    /// Short text about what broke.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// The exception behind it, if we had one.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>
    /// When it happened, UTC.
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Stamps the time on creation.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="exception"></param>
    public AudioErrorEventArgs(string message, Exception? exception = null)
    {
        Message = message;
        Exception = exception;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// One line for the log.
    /// </summary>
    public override string ToString()
    {
        if (Exception != null) { return $"Audio error at {Timestamp:HH:mm:ss.fff}: {Message} - {Exception.Message}"; }
        return $"Audio error at {Timestamp:HH:mm:ss.fff}: {Message}";
    }
}
