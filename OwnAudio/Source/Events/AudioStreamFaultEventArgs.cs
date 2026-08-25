using System;

namespace OwnaudioNET.Events
{
    /// <summary>
    /// What kind of stream fault the native side hit.
    /// </summary>
    public enum AudioStreamFaultKind
    {
        /// <summary>
        /// Device is gone (unplug, sleep/wake, rate change). Stream stopped, needs reopen.
        /// </summary>
        DeviceNotAvailable = 1,

        /// <summary>
        /// Some other backend error, not a plain device removal.
        /// </summary>
        BackendSpecific = 2,
    }

    /// <summary>
    /// Which side of the device faulted.
    /// </summary>
    public enum AudioStreamDirection
    {
        /// <summary>
        /// Playback stream.
        /// </summary>
        Output = 0,

        /// <summary>
        /// Capture stream, shared by every live input source.
        /// </summary>
        Input = 1,
    }

    /// <summary>
    /// Native stream died on us. The backend records the error on its own callback,
    /// the mixer control tick polls it and fires this. Without it the stream just goes
    /// silent and nobody notices.
    /// </summary>
    public class AudioStreamFaultEventArgs : EventArgs
    {
        /// <summary>
        /// Which flavour of fault this is.
        /// </summary>
        public AudioStreamFaultKind Kind { get; }

        /// <summary>
        /// Playback or capture.
        /// </summary>
        public AudioStreamDirection Direction { get; }

        /// <summary>
        /// Total errors on this stream since it opened, keeps climbing.
        /// </summary>
        public ulong ErrorCount { get; }

        /// <summary>
        /// When we built this event, UTC.
        /// </summary>
        public DateTime EventTimestamp { get; }

        /// <summary>
        /// Stamps the time on creation.
        /// </summary>
        /// <param name="kind"></param>
        /// <param name="errorCount"></param>
        /// <param name="direction">playback unless the capture stream faulted</param>
        public AudioStreamFaultEventArgs(AudioStreamFaultKind kind, ulong errorCount,
            AudioStreamDirection direction = AudioStreamDirection.Output)
        {
            Kind = kind;
            ErrorCount = errorCount;
            Direction = direction;
            EventTimestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// One line for the log.
        /// </summary>
        public override string ToString()
        {
            return $"Audio Stream Fault: {Direction} {Kind} (error #{ErrorCount}) at {EventTimestamp:O}";
        }
    }
}
