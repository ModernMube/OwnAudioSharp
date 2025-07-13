using Ownaudio.Utilities.OwnChordDetect.Core;
using System.Collections.Generic;
using System.Linq;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Utilities.OwnChordDetect.Detectors
{
    /// <summary>
    /// Extended chord detector with additional chord types.
    /// </summary>
    public class ExtendedChordDetector : BaseChordDetector
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ExtendedChordDetector"/> class with extended chord templates.
        /// </summary>
        /// <param name="confidenceThreshold">The minimum confidence threshold for chord detection. Default is 0.6f.</param>
        public ExtendedChordDetector(float confidenceThreshold = 0.6f) : base(confidenceThreshold)
        {
            AddExtendedTemplates();
        }

        /// <summary>
        /// Adds extended chord templates (suspended, diminished, augmented, add9) to the detector's template collection.
        /// </summary>
        private void AddExtendedTemplates()
        {
            var extendedTemplates = ChordTemplates.CreateExtendedTemplates();
            foreach (var (name, template) in extendedTemplates)
            {
                _chordTemplates[name] = template;
            }
        }

        /// <summary>
        /// Analyzes notes and returns the top N chord matches ranked by confidence.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for chord detection.</param>
        /// <param name="topN">The number of top matches to return. Default is 5.</param>
        /// <returns>A list of tuples containing chord names and their confidence scores, ordered by descending confidence.</returns>
        public List<(string chord, float confidence)> GetTopMatches(List<Note> notes, int topN = 5)
        {
            if (!notes.Any())
                return new List<(string, float)> { ("N", 0.9f) };

            var chromagram = ComputeChromagram(notes);
            var similarities = AnalyzeAllSimilarities(chromagram);

            return similarities.Take(topN).Select(kvp => (kvp.Key, kvp.Value)).ToList();
        }
    }
}
