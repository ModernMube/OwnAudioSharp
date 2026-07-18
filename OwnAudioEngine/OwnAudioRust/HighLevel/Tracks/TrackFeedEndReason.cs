namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Why a <see cref="TrackFeeder"/> pump loop called it a day.
/// </summary>
public enum TrackFeedEndReason
{
    /// <summary>
    /// Decoder hit the end and everything decoded made it into the feed.
    /// </summary>
    EndOfStream,

    /// <summary>
    /// Someone called Stop or Dispose before the stream ran out.
    /// </summary>
    Stopped,

    /// <summary>
    /// The decoder or the track threw; see <see cref="TrackFeedCompletedEventArgs.Error"/>.
    /// </summary>
    Faulted,
}
