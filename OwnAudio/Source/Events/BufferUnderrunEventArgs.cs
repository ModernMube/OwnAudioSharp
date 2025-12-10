namespace OwnaudioNET.Events;

/// <summary>
/// Event arguments for buffer underrun events.
/// </summary>
public sealed class BufferUnderrunEventArgs : EventArgs
{
    /// <summary>
    /// Gets the number of frames that were missed due to underrun.
    /// </summary>
    public int MissedFrames { get; }

    /// <summary>
    /// Gets the timestamp when the underrun occurred.
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Gets the position in the audio stream where the underrun occurred (in frames).
    /// </summary>
    public long Position { get; }

    public BufferUnderrunEventArgs(int missedFrames, long position)
    {
        MissedFrames = missedFrames;
        Position = position;
        Timestamp = DateTime.UtcNow;
    }

    public override string ToString()
    {
        return $"Buffer underrun: {MissedFrames} frames missed at position {Position} ({Timestamp:HH:mm:ss.fff})";
    }
}
