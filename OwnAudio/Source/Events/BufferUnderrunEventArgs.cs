namespace OwnaudioNET.Events;

/// <summary>
/// We ran dry, the callback wanted samples we did not have.
/// </summary>
public sealed class BufferUnderrunEventArgs : EventArgs
{
    /// <summary>
    /// How many frames we lost.
    /// </summary>
    public int MissedFrames { get; }

    /// <summary>
    /// When it happened, UTC.
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Stream position in frames where we dropped out.
    /// </summary>
    public long Position { get; }

    /// <summary>
    /// Stamps the time on creation.
    /// </summary>
    /// <param name="missedFrames"></param>
    /// <param name="position"></param>
    public BufferUnderrunEventArgs(int missedFrames, long position)
    {
        MissedFrames = missedFrames;
        Position = position;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// One line for the log.
    /// </summary>
    public override string ToString()
    {
        return $"Buffer underrun: {MissedFrames} frames missed at position {Position} ({Timestamp:HH:mm:ss.fff})";
    }
}
