using OwnaudioNET.Core;

namespace OwnaudioNET.Events;

/// <summary>
/// Fired when the player flips from one state to another.
/// </summary>
public sealed class AudioStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Where we came from.
    /// </summary>
    public AudioState OldState { get; }

    /// <summary>
    /// Where we are now.
    /// </summary>
    public AudioState NewState { get; }

    /// <summary>
    /// When the switch happened, UTC.
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Stamps the time on creation.
    /// </summary>
    /// <param name="oldState"></param>
    /// <param name="newState"></param>
    public AudioStateChangedEventArgs(AudioState oldState, AudioState newState)
    {
        OldState = oldState;
        NewState = newState;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// One line for the log.
    /// </summary>
    public override string ToString()
    {
        return $"State changed from {OldState} to {NewState} at {Timestamp:HH:mm:ss.fff}";
    }
}
