using System;

namespace OwnaudioNET.Events
{
    /// <summary>
    /// Classifies an audio-stream fault surfaced by the native backend.
    /// </summary>
    public enum AudioStreamFaultKind
    {
        /// <summary>
        /// The audio device is no longer available (unplugged, disabled, or lost on
        /// sleep/wake or a sample-rate change). The stream has stopped; audio will
        /// not resume until the stream is reopened on an available device.
        /// </summary>
        DeviceNotAvailable = 1,

        /// <summary>
        /// A backend-specific error that is not a plain device removal.
        /// </summary>
        BackendSpecific = 2,
    }

    /// <summary>
    /// Event arguments for a native audio-stream fault (device loss or backend
    /// error) detected while the Rust-native output stream is running.
    /// </summary>
    /// <remarks>
    /// The native backend delivers stream errors on an internal callback that the
    /// core records into a lock-free state; the mixer's control-rate tick polls it
    /// and raises this event when a new fault is seen. This is the "the audio went
    /// silent" signal that a long-running host (sleep/wake cycles, device changes)
    /// needs in order to recover, instead of the stream dying unobserved.
    /// </remarks>
    public class AudioStreamFaultEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the classification of the fault.
        /// </summary>
        public AudioStreamFaultKind Kind { get; }

        /// <summary>
        /// Gets the monotonic total number of errors the backend has reported on
        /// the stream since it opened.
        /// </summary>
        public ulong ErrorCount { get; }

        /// <summary>
        /// Gets the UTC time this event was created.
        /// </summary>
        public DateTime EventTimestamp { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioStreamFaultEventArgs"/> class.
        /// </summary>
        /// <param name="kind">The classification of the fault.</param>
        /// <param name="errorCount">The total error count reported on the stream.</param>
        public AudioStreamFaultEventArgs(AudioStreamFaultKind kind, ulong errorCount)
        {
            Kind = kind;
            ErrorCount = errorCount;
            EventTimestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Returns a string representation of the fault event.
        /// </summary>
        public override string ToString()
        {
            return $"Audio Stream Fault: {Kind} (error #{ErrorCount}) at {EventTimestamp:O}";
        }
    }
}
