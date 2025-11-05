using OwnaudioLegacy.Utilities.Extensions;
using OwnaudioLegacy.Utilities.OwnChordDetect.Analysis;
using OwnaudioLegacy.Utilities.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OwnaudioLegacy.Utilities.OwnChordDetect.Detectors
{
    /// <summary>
    /// Detection modes for the chord detector.
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public enum DetectionMode
    {
        /// <summary>
        /// Basic chord detection with triads and 7th chords only.
        /// </summary>
        Basic,

        /// <summary>
        /// Extended chord detection including 9th, 11th, and 13th chords.
        /// </summary>
        Extended,

        /// <summary>
        /// Key-aware chord detection with appropriate note naming.
        /// </summary>
        KeyAware,

        /// <summary>
        /// Optimized detection with ambiguity analysis and stability checking.
        /// </summary>
        Optimized
    }

    /// <summary>
    /// Unified chord detector with multiple detection modes.
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class ChordDetector
    {
        /// <summary>
        /// Dictionary of chord templates mapping chord names to their normalized chromagram patterns.
        /// </summary>
        private Dictionary<string, float[]> _templates;

        /// <summary>
        /// The minimum confidence threshold for chord detection (0.0 to 1.0).
        /// </summary>
        private readonly float _confidenceThreshold;

        /// <summary>
        /// The threshold for determining chord ambiguity in optimized mode.
        /// </summary>
        private readonly float _ambiguityThreshold;

        /// <summary>
        /// The key detector instance for musical key analysis.
        /// </summary>
        private readonly KeyDetector _keyDetector;

        /// <summary>
        /// The currently active musical key for chord naming.
        /// </summary>
        private MusicalKey _currentKey;

        /// <summary>
        /// The detection mode used for chord analysis.
        /// </summary>
        private readonly DetectionMode _mode;

        /// <summary>
        /// Buffer for storing recent note groups for stability analysis.
        /// </summary>
        private readonly Queue<List<Note>> _noteBuffer;

        /// <summary>
        /// The maximum size of the note buffer for real-time processing.
        /// </summary>
        private readonly int _bufferSize;

        /// <summary>
        /// Initializes a new instance of the ChordDetector class.
        /// </summary>
        /// <param name="mode">The detection mode to use for chord analysis.</param>
        /// <param name="confidenceThreshold">The minimum confidence threshold for chord detection (0.0 to 1.0).</param>
        /// <param name="ambiguityThreshold">The threshold for determining chord ambiguity in optimized mode.</param>
        /// <param name="bufferSize">The maximum size of the note buffer for real-time processing.</param>
        #nullable disable
        public ChordDetector(DetectionMode mode = DetectionMode.Extended,
                            float confidenceThreshold = 0.6f,
                            float ambiguityThreshold = 0.1f,
                            int bufferSize = 5)
        {
            _mode = mode;
            _confidenceThreshold = confidenceThreshold;
            _ambiguityThreshold = ambiguityThreshold;
            _keyDetector = new KeyDetector();
            _noteBuffer = new Queue<List<Note>>();
            _bufferSize = bufferSize;
            UpdateTemplates();
        }
        #nullable restore

        /// <summary>
        /// Analyzes a list of notes and returns the detected chord with analysis details.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for chord detection.</param>
        /// <returns>A ChordAnalysis object containing the detected chord, confidence, and detailed analysis.</returns>
        public ChordAnalysis AnalyzeChord(List<Note> notes)
        {
            if (!notes.Any())
                return new ChordAnalysis("N", 0.9f, "No notes detected", new string[0]);

            var chromagram = ComputeChromagram(notes);
            var (chord, confidence, isAmbiguous, alternatives) = DetectChordAdvancedBase(chromagram);

            var pitchClasses = notes.Select(n => n.Pitch % 12).Distinct().OrderBy(x => x).ToArray();
            var noteNames = pitchClasses.Select(pc => ChordTemplates.GetNoteName(pc, _currentKey)).ToArray();

            return new ChordAnalysis(chord, confidence, GenerateExplanation(pitchClasses, chord, confidence, isAmbiguous), noteNames)
            {
                IsAmbiguous = isAmbiguous,
                Alternatives = alternatives,
                PitchClasses = pitchClasses,
                Chromagram = chromagram
            };
        }

        /// <summary>
        /// Analyzes chord with key-aware naming.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for chord detection.</param>
        /// <returns>A ChordAnalysis object with key-appropriate note naming.</returns>
        public ChordAnalysis AnalyzeChordWithKey(List<Note> notes)
        {
            return AnalyzeChord(notes);
        }

        /// <summary>
        /// Analyzes the entire song to detect its key and then analyzes individual chords.
        /// </summary>
        /// <param name="allSongNotes">The complete list of notes from the entire song for key detection.</param>
        /// <param name="chordNotes">The specific notes to analyze for chord detection.</param>
        /// <returns>A ChordAnalysis object with key-appropriate chord naming based on the song context.</returns>
        public ChordAnalysis AnalyzeChordInSongContext(List<Note> allSongNotes, List<Note> chordNotes)
        {
            if (_currentKey == null)
            {
                _currentKey = _keyDetector.DetectKey(allSongNotes);
                UpdateTemplates();
            }

            return AnalyzeChord(chordNotes);
        }

        /// <summary>
        /// Sets the musical key explicitly.
        /// </summary>
        /// <param name="key">The musical key to use for chord naming.</param>
        public void SetKey(MusicalKey key)
        {
            _currentKey = key;
            UpdateTemplates();
        }

        /// <summary>
        /// Gets the currently detected or set musical key.
        /// </summary>
        /// <returns>The current musical key, or null if no key has been detected or set.</returns>
        public MusicalKey GetCurrentKey() => _currentKey;

        /// <summary>
        /// Detects the key from a collection of notes.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for key detection.</param>
        /// <returns>The detected musical key.</returns>
        public MusicalKey DetectKeyFromNotes(List<Note> notes)
        {
            return _keyDetector.DetectKey(notes);
        }

        /// <summary>
        /// Processes notes for real-time detection with stability analysis.
        /// </summary>
        /// <param name="newNotes">The new notes to add to the processing buffer.</param>
        /// <returns>A tuple containing the most stable chord name and its stability score (0.0 to 1.0).</returns>
        public (string chord, float stability) ProcessNotes(List<Note> newNotes)
        {
            _noteBuffer.Enqueue(newNotes);

            if (_noteBuffer.Count > _bufferSize)
                _noteBuffer.Dequeue();

            var recentChords = new List<string>();

            foreach (var noteGroup in _noteBuffer)
            {
                var (chord, confidence, _, _) = DetectChordAdvancedBase(ComputeChromagram(noteGroup));
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

        /// <summary>
        /// Gets top N chord matches.
        /// </summary>
        /// <param name="notes">The list of notes to analyze.</param>
        /// <param name="topN">The number of top matches to return.</param>
        /// <returns>A list of tuples containing chord names and their confidence scores, ordered by confidence.</returns>
        public List<(string chord, float confidence)> GetTopMatches(List<Note> notes, int topN = 5)
        {
            if (!notes.Any())
                return new List<(string, float)> { ("N", 0.9f) };

            var chromagram = ComputeChromagram(notes);
            var similarities = AnalyzeAllSimilarities(chromagram);

            return similarities.Take(topN).Select(kvp => (kvp.Key, kvp.Value)).ToList();
        }

        /// <summary>
        /// Detects chord from chromagram with advanced analysis.
        /// </summary>
        /// <param name="chromagram">The 12-element chromagram array representing pitch class distribution.</param>
        /// <returns>A tuple containing the detected chord name and confidence score.</returns>
        public (string chord, float confidence) DetectChordFromChromagram(float[] chromagram)
        {
            var (chord, confidence, _, _) = DetectChordAdvancedBase(chromagram);
            return (chord, confidence);
        }

        /// <summary>
        /// Adds a custom chord template.
        /// </summary>
        /// <param name="chordName">The name of the custom chord.</param>
        /// <param name="pitchClasses">The pitch classes that form the chord (0-11).</param>
        public void AddChordTemplate(string chordName, int[] pitchClasses)
        {
            _templates[chordName] = ChordTemplates.CreateTemplate(pitchClasses);
        }

        /// <summary>
        /// Gets all chord templates.
        /// </summary>
        /// <returns>A dictionary copy of all chord templates mapping chord names to their chromagram patterns.</returns>
        public Dictionary<string, float[]> GetChordTemplates()
        {
            return new Dictionary<string, float[]>(_templates);
        }

        /// <summary>
        /// Updates templates based on current mode and key.
        /// </summary>
        private void UpdateTemplates()
        {
            var includeExtended = _mode != DetectionMode.Basic;
            _templates = ChordTemplates.CreateAllTemplates(_currentKey, includeExtended);
        }

        /// <summary>
        /// Advanced chord detection with ambiguity analysis.
        /// </summary>
        /// <param name="chromagram">The chromagram to analyze for chord detection.</param>
        /// <returns>A tuple containing the chord name, confidence, ambiguity flag, and alternative chord names.</returns>
        protected (string chord, float confidence, bool isAmbiguous, string[] alternatives) DetectChordAdvancedBase(float[]? chromagram)
        {
            #nullable disable
            var similarities = AnalyzeAllSimilarities(chromagram);
            var topMatches = similarities.Take(5).ToArray();
            var bestMatch = topMatches.First();
            #nullable restore

            // Check for ambiguity if in optimized mode
            if (_mode == DetectionMode.Optimized)
            {
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

            // Simple detection for other modes
            var chord = bestMatch.Value >= _confidenceThreshold ? bestMatch.Key : "Unknown";
            return (chord, bestMatch.Value, false, new string[0]);
        }

        /// <summary>
        /// Computes chromagram from notes.
        /// </summary>
        /// <param name="notes">The list of notes to convert to chromagram.</param>
        /// <returns>A normalized 12-element array representing the pitch class distribution.</returns>
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
        /// Analyzes similarity between chromagram and all templates.
        /// </summary>
        /// <param name="chromagram">The chromagram to compare against all chord templates.</param>
        /// <returns>A dictionary of chord names and their similarity scores, ordered by similarity.</returns>
        private Dictionary<string, float> AnalyzeAllSimilarities(float[] chromagram)
        {
            var results = new Dictionary<string, float>();

            foreach (var (chordName, template) in _templates)
            {
                results[chordName] = ComputeCosineSimilarity(chromagram, template);
            }

            return results.OrderByDescending(kvp => kvp.Value).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Computes cosine similarity between two vectors.
        /// </summary>
        /// <param name="vector1">The first vector for similarity calculation.</param>
        /// <param name="vector2">The second vector for similarity calculation.</param>
        /// <returns>The cosine similarity score between 0.0 and 1.0.</returns>
        /// <exception cref="ArgumentException">Thrown when vectors have different lengths.</exception>
        private float ComputeCosineSimilarity(float[] vector1, float[] vector2)
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

        /// <summary>
        /// Generates explanation for chord analysis.
        /// </summary>
        /// <param name="pitchClasses">The pitch classes present in the chord.</param>
        /// <param name="chord">The detected chord name.</param>
        /// <param name="confidence">The confidence score of the detection.</param>
        /// <param name="isAmbiguous">Whether the chord detection is ambiguous.</param>
        /// <returns>A human-readable explanation of the chord analysis.</returns>
        private string GenerateExplanation(int[] pitchClasses, string chord, float confidence, bool isAmbiguous)
        {
            var noteNames = pitchClasses.Select(pc => ChordTemplates.GetNoteName(pc, _currentKey)).ToArray();
            var keyInfo = _currentKey != null ? $" (Key: {_currentKey})" : "";

            if (pitchClasses.Length < 2)
                return $"Too few notes ({pitchClasses.Length}) for reliable chord detection{keyInfo}.";

            if (isAmbiguous)
                return $"Ambiguous chord with notes [{string.Join(", ", noteNames)}]{keyInfo}. Multiple interpretations possible.";

            return confidence switch
            {
                >= 0.9f => $"Clear {chord} chord with notes [{string.Join(", ", noteNames)}]{keyInfo}.",
                >= 0.7f => $"Likely {chord} chord with notes [{string.Join(", ", noteNames)}]{keyInfo}.",
                >= 0.5f => $"Possible {chord} chord with notes [{string.Join(", ", noteNames)}]{keyInfo}, but uncertain.",
                _ => $"Unclear chord with notes [{string.Join(", ", noteNames)}]{keyInfo}. Consider adding more notes."
            };
        }
    }
}
