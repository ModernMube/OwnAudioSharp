namespace Ownaudio.Sources;

/// <summary>
/// Enumeration represents audio playback state.
/// </summary>
public enum SourceState
{
    /// <summary>
    /// Indicates that the playback state is currently idle.
    /// </summary>
    Idle,

    /// <summary>
    /// Indicates that the playback state is currently playing an audio.
    /// </summary>
    Playing,

    /// <summary>
    /// Indicates that the playback is currently buferring.
    /// </summary>
    Buffering,

    /// <summary>
    /// Indicates that the playback state is currently paused.
    /// </summary>
    Paused,

    /// <summary>
    /// Indicates that you are currently recording audio in the recording state.
    /// </summary>
    Recording
}
