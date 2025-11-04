using OwnaudioNET.Core;

namespace OwnaudioNET.Events;

/// <summary>
/// Event arguments for audio state changes.
/// </summary>
public sealed class AudioStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the previous state.
    /// </summary>
    public AudioState OldState { get; }

    /// <summary>
    /// Gets the new state.
    /// </summary>
    public AudioState NewState { get; }

    /// <summary>
    /// Gets the timestamp when the state changed.
    /// </summary>
    public DateTime Timestamp { get; }

    public AudioStateChangedEventArgs(AudioState oldState, AudioState newState)
    {
        OldState = oldState;
        NewState = newState;
        Timestamp = DateTime.UtcNow;
    }

    public override string ToString()
    {
        return $"State changed from {OldState} to {NewState} at {Timestamp:HH:mm:ss.fff}";
    }
}
