using Ownaudio.Utilities.Extensions;
using Ownaudio.Utilities.OwnChordDetect.Analysis;
using Ownaudio.Utilities.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ownaudio.Utilities.OwnChordDetect.Detectors
{
    /// <summary>
    /// Base chord detector with core functionality.
    /// </summary>
    public class BaseChordDetector
    {
        /// <summary>
        /// Dictionary containing chord templates mapped by chord name.
        /// </summary>
        protected readonly Dictionary<string, float[]> _chordTemplates;

        /// <summary>
        /// The minimum confidence threshold required for a chord to be considered detected.
        /// </summary>
        protected readonly float _confidenceThreshold;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseChordDetector"/> class.
        /// </summary>
        /// <param name="confidenceThreshold">The minimum confidence threshold for chord detection. Default is 0.7f.</param>
        public BaseChordDetector(float confidenceThreshold = 0.7f)
        {
            _confidenceThreshold = confidenceThreshold;
            _chordTemplates = ChordTemplates.CreateBasicTemplates();
        }

        /// <summary>
        /// Analyzes a list of notes and returns the detected chord with analysis details.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for chord detection.</param>
        /// <returns>A <see cref="ChordAnalysis"/> object containing the detected chord and analysis information.</returns>
        public virtual ChordAnalysis AnalyzeChord(List<Note> notes)
        {
            if (!notes.Any())
                return new ChordAnalysis("N", 0.9f, "No notes detected", new string[0]);

            var chromagram = ComputeChromagram(notes);
            var (chord, confidence) = DetectChordFromChromagram(chromagram);

            var pitchClasses = notes.Select(n => n.Pitch % 12).Distinct().OrderBy(x => x).ToArray();
            var noteNames = pitchClasses.Select(ChordTemplates.GetNoteName).ToArray();

            return new ChordAnalysis(chord, confidence, "", noteNames)
            {
                IsAmbiguous = false,
                Alternatives = new string[] { },
                PitchClasses = pitchClasses,
                Chromagram = chromagram
            };
        }

        /// <summary>
        /// Detects the best matching chord from a chromagram using template matching.
        /// </summary>
        /// <param name="chromagram">A 12-element array representing the chromagram (pitch class distribution).</param>
        /// <returns>A tuple containing the chord name and confidence score.</returns>
        /// <exception cref="ArgumentException">Thrown when the chromagram is not a 12-element array.</exception>
        public (string chord, float confidence) DetectChordFromChromagram(float[] chromagram)
        {
            if (chromagram?.Length != 12)
                throw new ArgumentException("Chromagram must be a 12-element array");

            var bestMatch = "";
            var bestScore = 0.0f;

            foreach (var (chordName, template) in _chordTemplates)
            {
                var similarity = ComputeCosineSimilarity(chromagram, template);

                if (similarity > bestScore)
                {
                    bestScore = similarity;
                    bestMatch = chordName;
                }
            }

            return bestScore >= _confidenceThreshold ? (bestMatch, bestScore) : ("Unknown", bestScore);
        }

        /// <summary>
        /// Computes a chromagram (pitch class histogram) from a list of notes.
        /// </summary>
        /// <param name="notes">The list of notes to process.</param>
        /// <returns>A normalized 12-element float array representing the chromagram.</returns>
        public float[] ComputeChromagram(List<Note> notes)
        {
            var chroma = new float[12];

            foreach (var note in notes)
            {
                var pitchClass = note.Pitch % 12;
                chroma[pitchClass] += note.Amplitude;
            }

            // Normalize
            var sum = chroma.Sum();
            if (sum > 0)
            {
                for (int i = 0; i < 12; i++)
                {
                    chroma[i] /= sum;
                }
            }

            return chroma;
        }

        /// <summary>
        /// Adds a custom chord template to the detector's template collection.
        /// </summary>
        /// <param name="chordName">The name of the chord (e.g., "Cmaj9").</param>
        /// <param name="pitchClasses">Array of pitch classes that define the chord.</param>
        public void AddChordTemplate(string chordName, int[] pitchClasses)
        {
            _chordTemplates[chordName] = ChordTemplates.CreateTemplate(pitchClasses);
        }

        /// <summary>
        /// Gets a copy of all chord templates currently loaded in the detector.
        /// </summary>
        /// <returns>A dictionary mapping chord names to their template arrays.</returns>
        public Dictionary<string, float[]> GetChordTemplates()
        {
            return new Dictionary<string, float[]>(_chordTemplates);
        }

        /// <summary>
        /// Analyzes similarity between a chromagram and all chord templates.
        /// </summary>
        /// <param name="chromagram">A 12-element array representing the chromagram to analyze.</param>
        /// <returns>A dictionary mapping chord names to their similarity scores, ordered by descending similarity.</returns>
        public Dictionary<string, float> AnalyzeAllSimilarities(float[] chromagram)
        {
            var results = new Dictionary<string, float>();

            foreach (var (chordName, template) in _chordTemplates)
            {
                results[chordName] = ComputeCosineSimilarity(chromagram, template);
            }

            return results.OrderByDescending(kvp => kvp.Value).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Computes the cosine similarity between two vectors.
        /// </summary>
        /// <param name="vector1">The first vector.</param>
        /// <param name="vector2">The second vector.</param>
        /// <returns>The cosine similarity score between 0.0 and 1.0.</returns>
        /// <exception cref="ArgumentException">Thrown when the vectors have different lengths.</exception>
        protected float ComputeCosineSimilarity(float[] vector1, float[] vector2)
        {
            if (vector1.Length != vector2.Length)
                throw new ArgumentException("Vectors must have the same length");

            var dotProduct = 0.0f;
            var magnitude1 = 0.0f;
            var magnitude2 = 0.0f;

            for (int i = 0; i < vector1.Length; i++)
            {
                dotProduct += vector1[i] * vector2[i];
                magnitude1 += vector1[i] * vector1[i];
                magnitude2 += vector2[i] * vector2[i];
            }

            var denominator = Math.Sqrt(magnitude1) * Math.Sqrt(magnitude2);
            return denominator > 0 ? (float)(dotProduct / denominator) : 0.0f;
        }
    }
}
