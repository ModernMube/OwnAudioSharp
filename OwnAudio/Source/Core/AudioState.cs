namespace OwnaudioNET.Core;

/// <summary>
/// Where a source currently stands in its playback lifecycle.
/// </summary>
public enum AudioState
{
    Stopped,

    Playing,

    /// <summary>
    /// Held, but resumable from the same spot.
    /// </summary>
    Paused,

    /// <summary>
    /// Ran out of audio to hand over.
    /// </summary>
    EndOfStream,

    Error
}
