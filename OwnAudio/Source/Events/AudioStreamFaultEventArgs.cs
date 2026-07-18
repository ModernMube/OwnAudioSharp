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
    /// Native output stream died on us. The backend records the error on its own
    /// callback, the mixer control tick polls it and fires this. Without it the
    /// stream just goes silent and nobody notices.
    /// </summary>
    public class AudioStreamFaultEventArgs : EventArgs
    {
        /// <summary>
        /// Which flavour of fault this is.
        /// </summary>
        public AudioStreamFaultKind Kind { get; }

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
        public AudioStreamFaultEventArgs(AudioStreamFaultKind kind, ulong errorCount)
        {
            Kind = kind;
            ErrorCount = errorCount;
            EventTimestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// One line for the log.
        /// </summary>
        public override string ToString()
        {
            return $"Audio Stream Fault: {Kind} (error #{ErrorCount}) at {EventTimestamp:O}";
        }
    }
}
