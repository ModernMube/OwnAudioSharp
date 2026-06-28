namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Describes why a <see cref="TrackFeeder"/> pump loop terminated.
/// </summary>
public enum TrackFeedEndReason
{
    /// <summary>
    /// The decoder reached the end of the stream and every decoded sample was
    /// pushed into the track's audio feed.
    /// </summary>
    EndOfStream,

    /// <summary>
    /// Feeding was stopped explicitly via <see cref="TrackFeeder.Stop"/> or
    /// <see cref="System.IDisposable.Dispose"/> before the stream ended.
    /// </summary>
    Stopped,

    /// <summary>
    /// The pump loop aborted because the decoder or the track raised an
    /// exception; see <see cref="TrackFeedCompletedEventArgs.Error"/>.
    /// </summary>
    Faulted,
}
