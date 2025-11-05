// RealTimeChordDetector.cs - simplified wrapper
using OwnaudioLegacy.Utilities.Extensions;
using System.Collections.Generic;

namespace OwnaudioLegacy.Utilities.OwnChordDetect.Detectors
{
    /// <summary>
    /// Real-time chord detector for continuous analysis with stability tracking.
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class RealTimeChordDetector
    {
        /// <summary>
        /// The underlying chord detector configured for optimized real-time analysis.
        /// </summary>
        private readonly ChordDetector _detector;

        /// <summary>
        /// Initializes a new instance of the RealTimeChordDetector class.
        /// </summary>
        /// <param name="bufferSize">The size of the internal buffer for stability analysis. Default is 5.</param>
        public RealTimeChordDetector(int bufferSize = 5)
        {
            _detector = new ChordDetector(DetectionMode.Optimized, bufferSize: bufferSize);
        }

        /// <summary>
        /// Processes a new group of notes and returns the most stable chord detected.
        /// </summary>
        /// <param name="newNotes">The new notes to add to the analysis buffer.</param>
        /// <returns>A tuple containing the most stable chord name and its stability score (0.0 to 1.0).</returns>
        public (string chord, float stability) ProcessNotes(List<Note> newNotes)
        {
            return _detector.ProcessNotes(newNotes);
        }
    }
}
