using System;

namespace OwnaudioNET.Events
{
    /// <summary>
    /// Event arguments for track dropout events.
    /// Provides detailed information about audio dropouts in master clock synchronized playback.
    /// </summary>
    public class TrackDropoutEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the unique identifier of the track that experienced the dropout.
        /// </summary>
        public Guid TrackId { get; }

        /// <summary>
        /// Gets the name/type of the track that experienced the dropout.
        /// </summary>
        public string TrackName { get; }

        /// <summary>
        /// Gets the master clock timestamp (in seconds) when the dropout occurred.
        /// </summary>
        public double MasterTimestamp { get; }

        /// <summary>
        /// Gets the master clock sample position when the dropout occurred.
        /// </summary>
        public long MasterSamplePosition { get; }

        /// <summary>
        /// Gets the number of frames that were missed/dropped.
        /// </summary>
        public int MissedFrames { get; }

        /// <summary>
        /// Gets the reason/cause of the dropout (e.g., "Buffer underrun", "Seek failed").
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Gets the timestamp when this event was created.
        /// </summary>
        public DateTime EventTimestamp { get; }

        /// <summary>
        /// Initializes a new instance of the TrackDropoutEventArgs class.
        /// </summary>
        /// <param name="trackId">The unique identifier of the affected track</param>
        /// <param name="trackName">The name/type of the affected track</param>
        /// <param name="masterTimestamp">The master clock timestamp in seconds</param>
        /// <param name="masterSamplePosition">The master clock sample position</param>
        /// <param name="missedFrames">The number of frames that were dropped</param>
        /// <param name="reason">The reason for the dropout</param>
        public TrackDropoutEventArgs(
            Guid trackId,
            string trackName,
            double masterTimestamp,
            long masterSamplePosition,
            int missedFrames,
            string reason)
        {
            TrackId = trackId;
            TrackName = trackName ?? string.Empty;
            MasterTimestamp = masterTimestamp;
            MasterSamplePosition = masterSamplePosition;
            MissedFrames = missedFrames;
            Reason = reason ?? string.Empty;
            EventTimestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Returns a string representation of the dropout event.
        /// </summary>
        public override string ToString()
        {
            return $"Track Dropout: {TrackName} (ID: {TrackId}) at {MasterTimestamp:F3}s " +
                   $"(Sample: {MasterSamplePosition}), Missed: {MissedFrames} frames, Reason: {Reason}";
        }
    }
}
