namespace OwnaudioNET.Core;

/// <summary>
/// Represents the playback state of an audio source.
/// </summary>
public enum AudioState
{
    /// <summary>
    /// Source is stopped and not processing audio.
    /// </summary>
    Stopped,

    /// <summary>
    /// Source is actively playing audio.
    /// </summary>
    Playing,

    /// <summary>
    /// Source is paused and can be resumed.
    /// </summary>
    Paused,

    /// <summary>
    /// Source has reached the end of the audio.
    /// </summary>
    EndOfStream,

    /// <summary>
    /// Source is in an error state.
    /// </summary>
    Error
}
