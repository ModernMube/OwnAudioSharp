using System;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// What came out of a <see cref="TrackFeeder"/> pump loop when it ended.
/// </summary>
public sealed class TrackFeedCompletedEventArgs : EventArgs
{
    #region Construction

    /// <summary>
    /// error is only set when reason is Faulted.
    /// </summary>
    /// <param name="reason"></param>
    /// <param name="error"></param>
    public TrackFeedCompletedEventArgs(TrackFeedEndReason reason, Exception? error = null)
    {
        Reason = reason;
        Error = error;
    }

    #endregion

    #region Propertyes

    /// <summary>
    /// Why the loop terminated.
    /// </summary>
    public TrackFeedEndReason Reason { get; }

    /// <summary>
    /// The exception that killed the feed, null otherwise.
    /// </summary>
    public Exception? Error { get; }

    #endregion
}
