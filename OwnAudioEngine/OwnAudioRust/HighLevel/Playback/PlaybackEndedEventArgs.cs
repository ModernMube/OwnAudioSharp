using System;

namespace Ownaudio.Audio.Playback;

/// <summary>
/// Describes why audio playback stopped in a <see cref="PlaybackEndedEventArgs"/> event.
/// </summary>
public enum PlaybackEndReason
{
    /// <summary>The audio reached its natural end.</summary>
    Finished,

    /// <summary>Playback was stopped programmatically via <see cref="AudioPlayer.Stop"/>.</summary>
    Stopped,

    /// <summary>Playback ended due to an underlying stream error.</summary>
    Error,
}

/// <summary>
/// Provides data for the <see cref="AudioPlayer.PlaybackEnded"/> event.
/// </summary>
public sealed class PlaybackEndedEventArgs : EventArgs
{
    #region Properties

    /// <summary>Indicates the reason playback stopped.</summary>
    public PlaybackEndReason Reason { get; }

    /// <summary>
    /// The exception that caused playback to end, or <see langword="null"/> if
    /// <see cref="Reason"/> is not <see cref="PlaybackEndReason.Error"/>.
    /// </summary>
    public Exception? Exception { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new <see cref="PlaybackEndedEventArgs"/> with the given reason.
    /// </summary>
    /// <param name="reason">The reason playback stopped.</param>
    /// <param name="exception">Optional exception for <see cref="PlaybackEndReason.Error"/> cases.</param>
    public PlaybackEndedEventArgs(PlaybackEndReason reason, Exception? exception = null)
    {
        Reason    = reason;
        Exception = exception;
    }

    #endregion
}
