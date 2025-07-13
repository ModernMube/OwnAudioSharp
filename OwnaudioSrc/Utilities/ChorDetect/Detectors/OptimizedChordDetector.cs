using Ownaudio.Utilities.OwnChordDetect.Analysis;
using Ownaudio.Utilities.OwnChordDetect.Core;
using Ownaudio.Utilities.Extensions;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ownaudio.Utilities.OwnChordDetect.Detectors
{
    /// <summary>
    /// Optimized chord detector with ambiguity handling.
    /// </summary>
    public class OptimizedChordDetector : ExtendedChordDetector
    {
        /// <summary>
        /// The threshold used to determine when multiple chord matches are considered ambiguous.
        /// </summary>
        private readonly float _ambiguityThreshold;

        /// <summary>
        /// Initializes a new instance of the <see cref="OptimizedChordDetector"/> class with ambiguity detection capabilities.
        /// </summary>
        /// <param name="confidenceThreshold">The minimum confidence threshold for chord detection. Default is 0.6f.</param>
        /// <param name="ambiguityThreshold">The threshold for detecting ambiguous chord matches. Default is 0.1f.</param>
        public OptimizedChordDetector(float confidenceThreshold = 0.6f, float ambiguityThreshold = 0.1f)
            : base(confidenceThreshold)
        {
            _ambiguityThreshold = ambiguityThreshold;
        }

        /// <summary>
        /// Performs advanced chord detection with ambiguity analysis and alternative suggestions.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for chord detection.</param>
        /// <returns>A tuple containing the detected chord name, confidence score, ambiguity flag, and alternative chord suggestions.</returns>
        public (string chord, float confidence, bool isAmbiguous, string[] alternatives) DetectChordAdvanced(List<Note> notes)
        {
            if (!notes.Any())
                return ("N", 0.9f, false, new string[0]);

            var chromagram = ComputeChromagram(notes);
            var similarities = AnalyzeAllSimilarities(chromagram);

            var topMatches = similarities.Take(5).ToArray();
            var bestMatch = topMatches.First();

            // Check for ambiguity
            var ambiguousMatches = topMatches
                .Where(kvp => Math.Abs(kvp.Value - bestMatch.Value) <= _ambiguityThreshold)
                .Select(kvp => kvp.Key)
                .ToArray();

            var isAmbiguous = ambiguousMatches.Length > 1;

            if (bestMatch.Value >= _confidenceThreshold)
            {
                if (isAmbiguous)
                {
                    var combinedName = string.Join("/", ambiguousMatches.Take(3));
                    return (combinedName, bestMatch.Value, true, ambiguousMatches);
                }
                else
                {
                    return (bestMatch.Key, bestMatch.Value, false, new string[0]);
                }
            }

            return ("Unknown", bestMatch.Value, false, topMatches.Take(3).Select(kvp => kvp.Key).ToArray());
        }

        /// <summary>
        /// Analyzes a list of notes and returns a comprehensive chord analysis with explanations and alternatives.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for chord detection.</param>
        /// <returns>A <see cref="ChordAnalysis"/> object containing detailed chord information including ambiguity status and explanations.</returns>
        public ChordAnalysis AnalyzeChord(List<Note> notes)
        {
            if (!notes.Any())
                return new ChordAnalysis("N", 0.9f, "No notes detected", new string[0]);

            var chromagram = ComputeChromagram(notes);
            var (chord, confidence, isAmbiguous, alternatives) = DetectChordAdvanced(notes);

            var pitchClasses = notes.Select(n => n.Pitch % 12).Distinct().OrderBy(x => x).ToArray();
            var noteNames = pitchClasses.Select(ChordTemplates.GetNoteName).ToArray();

            string explanation = GenerateExplanation(pitchClasses, chord, confidence, isAmbiguous);

            return new ChordAnalysis(chord, confidence, explanation, noteNames)
            {
                IsAmbiguous = isAmbiguous,
                Alternatives = alternatives,
                PitchClasses = pitchClasses,
                Chromagram = chromagram
            };
        }

        /// <summary>
        /// Generates a human-readable explanation of the chord analysis results.
        /// </summary>
        /// <param name="pitchClasses">Array of pitch classes present in the analyzed notes.</param>
        /// <param name="chord">The detected chord name.</param>
        /// <param name="confidence">The confidence score of the detection.</param>
        /// <param name="isAmbiguous">Flag indicating whether the chord detection is ambiguous.</param>
        /// <returns>A descriptive string explaining the chord analysis results and confidence level.</returns>
        private string GenerateExplanation(int[] pitchClasses, string chord, float confidence, bool isAmbiguous)
        {
            var noteNames = pitchClasses.Select(ChordTemplates.GetNoteName).ToArray();

            if (pitchClasses.Length < 2)
                return $"Too few notes ({pitchClasses.Length}) for reliable chord detection.";

            if (isAmbiguous)
                return $"Ambiguous chord with notes [{string.Join(", ", noteNames)}]. Multiple interpretations possible.";

            return confidence switch
            {
                >= 0.9f => $"Clear {chord} chord with notes [{string.Join(", ", noteNames)}].",
                >= 0.7f => $"Likely {chord} chord with notes [{string.Join(", ", noteNames)}].",
                >= 0.5f => $"Possible {chord} chord with notes [{string.Join(", ", noteNames)}], but uncertain.",
                _ => $"Unclear chord with notes [{string.Join(", ", noteNames)}]. Consider adding more notes."
            };
        }
    }
}
