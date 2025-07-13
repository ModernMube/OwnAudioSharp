using Ownaudio.Utilities.OwnChordDetect.Core;
using Ownaudio.Utilities.Extensions;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ownaudio.Utilities.OwnChordDetect.Detectors
{
    /// <summary>
    /// Real-time chord detector for continuous analysis.
    /// </summary>
    public class RealTimeChordDetector
    {
        /// <summary>
        /// The optimized chord detector used for analyzing individual note groups.
        /// </summary>
        private readonly OptimizedChordDetector _detector;

        /// <summary>
        /// A circular buffer that maintains a sliding window of recent note groups for stability analysis.
        /// </summary>
        private readonly Queue<List<Note>> _noteBuffer;

        /// <summary>
        /// The maximum number of note groups to keep in the buffer for temporal analysis.
        /// </summary>
        private readonly int _bufferSize;

        /// <summary>
        /// Initializes a new instance of the <see cref="RealTimeChordDetector"/> class for continuous chord analysis.
        /// </summary>
        /// <param name="bufferSize">The size of the sliding window buffer for stability analysis. Default is 5.</param>
        public RealTimeChordDetector(int bufferSize = 5)
        {
            _detector = new OptimizedChordDetector(confidenceThreshold: 0.6f);
            _noteBuffer = new Queue<List<Note>>();
            _bufferSize = bufferSize;
        }

        /// <summary>
        /// Processes a new group of notes and returns the most stable chord detected across the recent buffer.
        /// </summary>
        /// <param name="newNotes">The new group of notes to add to the analysis buffer.</param>
        /// <returns>A tuple containing the most common chord name and its stability score (0.0 to 1.0).</returns>
        public (string chord, float stability) ProcessNotes(List<Note> newNotes)
        {
            _noteBuffer.Enqueue(newNotes);

            if (_noteBuffer.Count > _bufferSize)
                _noteBuffer.Dequeue();

            var recentChords = new List<string>();

            foreach (var noteGroup in _noteBuffer)
            {
                var (chord, confidence, _, _) = _detector.DetectChordAdvanced(noteGroup);
                if (confidence > 0.5f)
                    recentChords.Add(chord);
            }

            if (!recentChords.Any())
                return ("Unknown", 0.0f);

            var chordCounts = recentChords
                .GroupBy(c => c)
                .OrderByDescending(g => g.Count())
                .ToArray();

            var mostCommon = chordCounts.First();
            var stability = (float)mostCommon.Count() / _noteBuffer.Count;

            return (mostCommon.Key, stability);
        }
    }
}
