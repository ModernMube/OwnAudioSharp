namespace Ownaudio.Audio.Playback;

/// <summary>
/// Represents the current playback state of an <see cref="AudioPlayer"/>.
/// </summary>
public enum PlaybackState
{
    /// <summary>No audio is loaded or the player has been stopped.</summary>
    Stopped,

    /// <summary>Audio is actively playing.</summary>
    Playing,

    /// <summary>Playback is paused; the position is preserved.</summary>
    Paused,
}
