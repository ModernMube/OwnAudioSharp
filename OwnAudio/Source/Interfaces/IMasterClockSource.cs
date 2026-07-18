using System;
using OwnaudioNET.Core;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Interfaces
{
    /// <summary>
    /// An audio source that can ride a master clock for sample-accurate sync.
    /// </summary>
    public interface IMasterClockSource : IAudioSource
    {
        /// <summary>
        /// Read frames lined up to a master-clock timestamp (seconds). Returns
        /// false on a dropout; result carries the details.
        /// </summary>
        /// <param name="masterTimestamp"></param>
        /// <param name="buffer"></param>
        /// <param name="frameCount"></param>
        /// <param name="result"></param>
        bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result);

        /// <summary>
        /// Where this track sits on the timeline (seconds). 0 = at the start,
        /// positive values push the start later.
        /// </summary>
        double StartOffset { get; set; }

        /// <summary>
        /// True while hooked to a master clock.
        /// </summary>
        bool IsAttachedToClock { get; }

        /// <summary>
        /// Hook this source onto a master clock.
        /// </summary>
        /// <param name="clock"></param>
        void AttachToClock(MasterClock clock);

        /// <summary>
        /// Unhook from the current clock.
        /// </summary>
        void DetachFromClock();
    }

    /// <summary>
    /// What a ReadSamplesAtTime call gave back.
    /// </summary>
    public struct ReadResult
    {
        /// <summary>
        /// True if it worked, false means a dropout (underrun).
        /// </summary>
        public bool Success;

        /// <summary>
        /// Frames actually read, may be short on a dropout.
        /// </summary>
        public int FramesRead;

        /// <summary>
        /// Error text when Success is false, otherwise null.
        /// </summary>
        public string? ErrorMessage;

        /// <summary>
        /// Success result.
        /// </summary>
        /// <param name="framesRead"></param>
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
        /// Failed result with a reason.
        /// </summary>
        /// <param name="framesRead"></param>
        /// <param name="errorMessage"></param>
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
