using System;
using OwnaudioNET.Core;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Interfaces
{
    /// <summary>
    /// Represents an audio source that can be synchronized to a master clock.
    /// Extends IAudioSource with timestamp-based reading and clock attachment.
    /// </summary>
    public interface IMasterClockSource : IAudioSource
    {
        /// <summary>
        /// Reads audio samples at a specific master clock timestamp.
        /// This method provides sample-accurate synchronization with the master clock.
        /// </summary>
        /// <param name="masterTimestamp">The current master clock timestamp in seconds</param>
        /// <param name="buffer">The buffer to fill with audio data</param>
        /// <param name="frameCount">The number of frames to read</param>
        /// <param name="result">Detailed result information including success status and frames read</param>
        /// <returns>True if read was successful (all frames provided), false if dropout occurred</returns>
        bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result);

        /// <summary>
        /// Gets or sets the start offset of this track on the master timeline (in seconds).
        /// A value of 0.0 means the track starts at the beginning of the timeline.
        /// Positive values delay the track start (e.g., 2.0 = track starts at 2 seconds).
        /// </summary>
        double StartOffset { get; set; }

        /// <summary>
        /// Gets whether this source is currently attached to a master clock.
        /// </summary>
        bool IsAttachedToClock { get; }

        /// <summary>
        /// Attaches this source to a master clock for synchronized playback.
        /// </summary>
        /// <param name="clock">The master clock to attach to</param>
        void AttachToClock(MasterClock clock);

        /// <summary>
        /// Detaches this source from its current master clock.
        /// </summary>
        void DetachFromClock();
    }

    /// <summary>
    /// Result structure for ReadSamplesAtTime operations.
    /// Provides detailed information about the read operation.
    /// </summary>
    public struct ReadResult
    {
        /// <summary>
        /// Indicates whether the read operation was successful.
        /// False indicates a dropout occurred (buffer underrun).
        /// </summary>
        public bool Success;

        /// <summary>
        /// The actual number of frames that were successfully read.
        /// May be less than requested if a dropout occurred.
        /// </summary>
        public int FramesRead;

        /// <summary>
        /// Error message if Success is false.
        /// Null or empty if operation was successful.
        /// </summary>
        public string? ErrorMessage;

        /// <summary>
        /// Creates a successful ReadResult.
        /// </summary>
        public static ReadResult CreateSuccess(int framesRead)
        {
            return new ReadResult
            {
                Success = true,
                FramesRead = framesRead,
                ErrorMessage = null
            };
        }

        /// <summary>
        /// Creates a failed ReadResult with an error message.
        /// </summary>
        public static ReadResult CreateFailure(int framesRead, string errorMessage)
        {
            return new ReadResult
            {
                Success = false,
                FramesRead = framesRead,
                ErrorMessage = errorMessage
            };
        }
    }
}
