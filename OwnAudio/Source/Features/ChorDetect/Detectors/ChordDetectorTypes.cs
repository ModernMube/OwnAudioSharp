using OwnaudioNET.Features.Extensions;

namespace OwnaudioNET.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// Old name for plain triad/7th detection, kept so existing code keeps compiling.
    /// </summary>
    public class BaseChordDetector : ChordDetector
    {
        /// <summary>
        /// confidenceThreshold is the minimum we accept before calling it a chord.
        /// </summary>
        public BaseChordDetector(float confidenceThreshold = 0.7f)
            : base(DetectionMode.Basic, confidenceThreshold) { }
    }

    /// <summary>
    /// Old name for the extended chord set.
    /// </summary>
    public class ExtendedChordDetector : ChordDetector
    {
        /// <summary>
        /// Looser default threshold, extended chords rarely match as cleanly.
        /// </summary>
        public ExtendedChordDetector(float confidenceThreshold = 0.6f)
            : base(DetectionMode.Extended, confidenceThreshold) { }
    }

    /// <summary>
    /// Old name for the optimized mode, this one also reports ambiguity.
    /// </summary>
    public class OptimizedChordDetector : ChordDetector
    {
        /// <summary>
        /// ambiguityThreshold is how close the runner-up may get before we flag the call as unsure.
        /// </summary>
        public OptimizedChordDetector(float confidenceThreshold = 0.6f, float ambiguityThreshold = 0.1f)
            : base(DetectionMode.Optimized, confidenceThreshold, ambiguityThreshold) { }

        /// <summary>
        /// Detection with the runner-ups attached.
        /// </summary>
        /// <returns>Chord, confidence, ambiguity flag and the alternative names.</returns>
        public (string chord, float confidence, bool isAmbiguous, string[] alternatives) DetectChordAdvanced(List<Note> notes)
        {
            return base.DetectChordAdvancedBase(ComputeChromagram(notes));
        }
    }

    /// <summary>
    /// Old name for key-aware mode — same detection, but sharps/flats get spelled properly.
    /// </summary>
    public class KeyAwareChordDetector : ChordDetector
    {
        /// <summary>
        /// Ambiguity off by default here, the key context usually settles it.
        /// </summary>
        public KeyAwareChordDetector(float confidenceThreshold = 0.6f, float ambiguityThreshold = 0.0f)
            : base(DetectionMode.KeyAware, confidenceThreshold, ambiguityThreshold) { }
    }
}
