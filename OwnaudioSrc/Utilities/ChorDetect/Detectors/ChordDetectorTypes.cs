using System.Collections.Generic;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Utilities.OwnChordDetect.Detectors
{
    /// <summary>
    /// Base chord detector - legacy compatibility wrapper for basic chord detection.
    /// </summary>
    public class BaseChordDetector : ChordDetector
    {
        /// <summary>
        /// Initializes a new instance of the BaseChordDetector class with basic detection mode.
        /// </summary>
        /// <param name="confidenceThreshold">The minimum confidence threshold for chord detection (0.0 to 1.0). Default is 0.7f.</param>
        public BaseChordDetector(float confidenceThreshold = 0.7f)
            : base(DetectionMode.Basic, confidenceThreshold) { }
    }

    /// <summary>
    /// Extended chord detector - legacy compatibility wrapper for extended chord detection.
    /// </summary>
    public class ExtendedChordDetector : ChordDetector
    {
        /// <summary>
        /// Initializes a new instance of the ExtendedChordDetector class with extended detection mode.
        /// </summary>
        /// <param name="confidenceThreshold">The minimum confidence threshold for chord detection (0.0 to 1.0). Default is 0.6f.</param>
        public ExtendedChordDetector(float confidenceThreshold = 0.6f)
            : base(DetectionMode.Extended, confidenceThreshold) { }
    }

    /// <summary>
    /// Optimized chord detector - legacy compatibility wrapper for optimized detection with ambiguity analysis.
    /// </summary>
    public class OptimizedChordDetector : ChordDetector
    {
        /// <summary>
        /// Initializes a new instance of the OptimizedChordDetector class with optimized detection mode.
        /// </summary>
        /// <param name="confidenceThreshold">The minimum confidence threshold for chord detection (0.0 to 1.0). Default is 0.6f.</param>
        /// <param name="ambiguityThreshold">The threshold for determining chord ambiguity. Default is 0.1f.</param>
        public OptimizedChordDetector(float confidenceThreshold = 0.6f, float ambiguityThreshold = 0.1f)
            : base(DetectionMode.Optimized, confidenceThreshold, ambiguityThreshold) { }

        /// <summary>
        /// Detects chord with advanced analysis including ambiguity detection and alternative chord suggestions.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for chord detection.</param>
        /// <returns>A tuple containing the detected chord name, confidence score, ambiguity flag, and alternative chord names.</returns>
        public (string chord, float confidence, bool isAmbiguous, string[] alternatives) DetectChordAdvanced(List<Note> notes)
        {
            var chromagram = ComputeChromagram(notes);
            return base.DetectChordAdvancedBase(chromagram);
        }
    }

    /// <summary>
    /// Key-aware chord detector - legacy compatibility wrapper for key-aware detection with appropriate note naming.
    /// </summary>
    public class KeyAwareChordDetector : ChordDetector
    {
        /// <summary>
        /// Initializes a new instance of the KeyAwareChordDetector class with key-aware detection mode.
        /// </summary>
        /// <param name="confidenceThreshold">The minimum confidence threshold for chord detection (0.0 to 1.0). Default is 0.6f.</param>
        /// <param name="ambiguityThreshold">The threshold for determining chord ambiguity. Default is 0.0f.</param>
        public KeyAwareChordDetector(float confidenceThreshold = 0.6f, float ambiguityThreshold = 0.0f)
            : base(DetectionMode.KeyAware, confidenceThreshold, ambiguityThreshold) { }
    }
}
