using System;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Carries the outcome of a <see cref="TrackFeeder"/> pump loop when it finishes.
/// </summary>
public sealed class TrackFeedCompletedEventArgs : EventArgs
{
    #region Construction

    /// <summary>
    /// Initializes a new <see cref="TrackFeedCompletedEventArgs"/>.
    /// </summary>
    /// <param name="reason">Why the pump loop terminated.</param>
    /// <param name="error">
    /// The exception that aborted feeding, or <see langword="null"/> unless
    /// <paramref name="reason"/> is <see cref="TrackFeedEndReason.Faulted"/>.
    /// </param>
    public TrackFeedCompletedEventArgs(TrackFeedEndReason reason, Exception? error = null)
    {
        Reason = reason;
        Error = error;
    }

    #endregion

    #region Properties

    /// <summary>Why the pump loop terminated.</summary>
    public TrackFeedEndReason Reason { get; }

    /// <summary>
    /// The exception that aborted feeding when <see cref="Reason"/> is
    /// <see cref="TrackFeedEndReason.Faulted"/>; otherwise <see langword="null"/>.
    /// </summary>
    public Exception? Error { get; }

    #endregion
}
