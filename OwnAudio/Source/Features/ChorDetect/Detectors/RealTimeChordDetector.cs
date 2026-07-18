using OwnaudioNET.Features.Extensions;
using System.Collections.Generic;

namespace OwnaudioNET.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// Chord detection for a running stream, keeps a short history so the answer doesn't flicker.
    /// </summary>
    public class RealTimeChordDetector
    {
        private readonly ChordDetector _detector;

        /// <summary>
        /// bufferSize is how many past windows the stability vote looks at.
        /// </summary>
        public RealTimeChordDetector(int bufferSize = 5, DetectionMode mode = DetectionMode.Optimized)
        {
            _detector = new ChordDetector(mode, bufferSize: bufferSize);
        }

        /// <summary>
        /// Feeds the next batch of notes in.
        /// </summary>
        /// <returns>The steadiest chord right now plus how sure we are, 0..1.</returns>
        public (string chord, float stability) ProcessNotes(List<Note> newNotes)
        {
            return _detector.ProcessNotes(newNotes);
        }
    }
}
