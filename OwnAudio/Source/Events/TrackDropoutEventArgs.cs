using System;

namespace OwnaudioNET.Events
{
    /// <summary>
    /// One track fell behind the master clock. Carries enough to tell which and why.
    /// </summary>
    public class TrackDropoutEventArgs : EventArgs
    {
        /// <summary>
        /// Id of the track that dropped.
        /// </summary>
        public Guid TrackId { get; }

        /// <summary>
        /// Track name or type, whatever the caller passed.
        /// </summary>
        public string TrackName { get; }

        /// <summary>
        /// Master clock time in seconds at the drop.
        /// </summary>
        public double MasterTimestamp { get; }

        /// <summary>
        /// Master clock sample pos at the drop.
        /// </summary>
        public long MasterSamplePosition { get; }

        /// <summary>
        /// How many frames we lost.
        /// </summary>
        public int MissedFrames { get; }

        /// <summary>
        /// Why it happened, e.g. "Buffer underrun" or "Seek failed".
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// When we built this event, UTC.
        /// </summary>
        public DateTime EventTimestamp { get; }

        /// <summary>
        /// Stamps the time on creation, null name/reason become empty.
        /// </summary>
        /// <param name="trackId"></param>
        /// <param name="trackName"></param>
        /// <param name="masterTimestamp"></param>
        /// <param name="masterSamplePosition"></param>
        /// <param name="missedFrames"></param>
        /// <param name="reason"></param>
        public TrackDropoutEventArgs(Guid trackId, string trackName, double masterTimestamp,
            long masterSamplePosition, int missedFrames, string reason)
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
        /// One line for the log.
        /// </summary>
        public override string ToString()
        {
            return $"Track Dropout: {TrackName} (ID: {TrackId}) at {MasterTimestamp:F3}s " +
                   $"(Sample: {MasterSamplePosition}), Missed: {MissedFrames} frames, Reason: {Reason}";
        }
    }
}
