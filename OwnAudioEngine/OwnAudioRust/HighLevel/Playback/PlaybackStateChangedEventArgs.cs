using System;

namespace Ownaudio.Audio.Playback;

/// <summary>
/// Provides data for the <see cref="AudioPlayer.StateChanged"/> event.
/// </summary>
public sealed class PlaybackStateChangedEventArgs : EventArgs
{
    #region Properties

    /// <summary>The state the player was in before the transition.</summary>
    public PlaybackState OldState { get; }

    /// <summary>The state the player has transitioned into.</summary>
    public PlaybackState NewState { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new <see cref="PlaybackStateChangedEventArgs"/> instance.
    /// </summary>
    /// <param name="oldState">The previous playback state.</param>
    /// <param name="newState">The new playback state.</param>
    public PlaybackStateChangedEventArgs(PlaybackState oldState, PlaybackState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    #endregion
}
