using System;
using System.Collections.Generic;
using System.Linq;

namespace Ownaudio.Utilities
{
    /// <summary>
    /// Provides comprehensive chord detection functionality from detected musical notes using pattern matching 
    /// against an extensive database of chord templates including triads, 7ths, extensions, and jazz harmonies.
    /// </summary>
    public class ChordDetector
    {
        /// <summary>
        /// Represents a detected musical note with timing, frequency, and identification information.
        /// </summary>
        public struct DetectedNote
        {
            /// <summary>
            /// Gets or sets the sample position where the note starts.
            /// </summary>
            public int StartSample;

            /// <summary>
            /// Gets or sets the sample position where the note ends.
            /// </summary>
            public int EndSample;

            /// <summary>
            /// Gets or sets the fundamental frequency of the detected note in Hz.
            /// </summary>
            public float Frequency;

            /// <summary>
            /// Gets or sets the amplitude (volume) of the detected note.
            /// </summary>
            public float Amplitude;

            /// <summary>
            /// Gets or sets the musical note name with octave (e.g., "C4", "F#3").
            /// </summary>
            public string NoteName;
        }

        /// <summary>
        /// Represents a detected chord with its constituent notes, name, and confidence metrics.
        /// </summary>
        public struct DetectedChord
        {
            /// <summary>
            /// Gets or sets the list of note names that comprise the detected chord.
            /// </summary>
            public List<string> Notes;

            /// <summary>
            /// Gets or sets the name of the detected chord (e.g., "Cmajor", "Am7", "F#dim").
            /// </summary>
            public string ChordName;

            /// <summary>
            /// Gets or sets the confidence score of the chord detection (0.0 to 1.0).
            /// </summary>
            public float Confidence;

            /// <summary>
            /// Gets or sets the total number of note occurrences that contributed to this chord detection.
            /// </summary>
            public int OccurrenceCount;
        }

        /// <summary>
        /// Comprehensive database of chord templates mapping chord names to their chromatic interval patterns.
        /// Includes basic triads, 7th chords, extensions, suspended chords, altered dominants, and jazz harmonies.
        /// </summary>
        private static readonly Dictionary<string, int[]> ChordTemplates = new Dictionary<string, int[]>
        {
            {"major", new int[] {0, 4, 7}},
            {"minor", new int[] {0, 3, 7}},
            {"dim", new int[] {0, 3, 6}},
            {"aug", new int[] {0, 4, 8}},
            {"sus2", new int[] {0, 2, 7}},
            {"sus4", new int[] {0, 5, 7}},
            {"6", new int[] {0, 4, 7, 9}},
            {"m6", new int[] {0, 3, 7, 9}},
            {"maj7", new int[] {0, 4, 7, 11}},
            {"min7", new int[] {0, 3, 7, 10}},
            {"dom7", new int[] {0, 4, 7, 10}},
            {"dim7", new int[] {0, 3, 6, 9}},
            {"half-dim7", new int[] {0, 3, 6, 10}},
            {"aug7", new int[] {0, 4, 8, 10}},
            {"minmaj7", new int[] {0, 3, 7, 11}},
            {"7sus4", new int[] {0, 5, 7, 10}},
            {"7sus2", new int[] {0, 2, 7, 10}},
            {"add9", new int[] {0, 4, 7, 2}},
            {"madd9", new int[] {0, 3, 7, 2}},
            {"9", new int[] {0, 4, 7, 10, 2}},
            {"maj9", new int[] {0, 4, 7, 11, 2}},
            {"min9", new int[] {0, 3, 7, 10, 2}},
            {"7b9", new int[] {0, 4, 7, 10, 1}},
            {"7#9", new int[] {0, 4, 7, 10, 3}},
            {"add11", new int[] {0, 4, 7, 5}},
            {"11", new int[] {0, 4, 7, 10, 2, 5}},
            {"maj11", new int[] {0, 4, 7, 11, 2, 5}},
            {"min11", new int[] {0, 3, 7, 10, 2, 5}},
            {"7#11", new int[] {0, 4, 7, 10, 6}},
            {"13", new int[] {0, 4, 7, 10, 2, 9}},
            {"maj13", new int[] {0, 4, 7, 11, 2, 9}},
            {"min13", new int[] {0, 3, 7, 10, 2, 9}},
            {"7b13", new int[] {0, 4, 7, 10, 8}},
            {"7alt", new int[] {0, 4, 7, 10, 1, 3, 6, 8}},
            {"7b5", new int[] {0, 4, 6, 10}},
            {"7#5", new int[] {0, 4, 8, 10}},
            {"aug9", new int[] {0, 4, 8, 10, 2}},
            {"6/9", new int[] {0, 4, 7, 9, 2}},
            {"m6/9", new int[] {0, 3, 7, 9, 2}},
            {"maj7#11", new int[] {0, 4, 7, 11, 6}},
            {"maj7b5", new int[] {0, 4, 6, 11}},
            {"min7b5", new int[] {0, 3, 6, 10}},
            {"quartal", new int[] {0, 5, 10}},
            {"quartal2", new int[] {0, 5, 10, 3}},
            {"cluster2", new int[] {0, 1, 2}},
            {"cluster3", new int[] {0, 1, 2, 3}},
            {"maj7#5", new int[] {0, 4, 8, 11}},
            {"min/maj7", new int[] {0, 3, 7, 11}},
            {"so what", new int[] {0, 5, 10, 3, 7}},
            {"mu major", new int[] {0, 2, 4, 7, 9}},
            {"5", new int[] {0, 7}},
            {"no3", new int[] {0, 7, 10}},
            {"maj7+", new int[] {0, 4, 8, 11}},
            {"dim/maj7", new int[] {0, 3, 6, 11}},
            {"13sus4", new int[] {0, 5, 7, 10, 2, 9}},
            {"9sus4", new int[] {0, 5, 7, 10, 2}},
            {"maj9#11", new int[] {0, 4, 7, 11, 2, 6}},
            {"min9#11", new int[] {0, 3, 7, 10, 2, 6}},
            {"neapolitan", new int[] {0, 1, 5}},
            {"aug6", new int[] {0, 4, 6, 10}},
            {"fr6", new int[] {0, 2, 6, 10}},
            {"ger6", new int[] {0, 4, 6, 10}},
            {"it6", new int[] {0, 4, 10}},
            {"dim/add7", new int[] {0, 3, 6, 10}},
            {"dim/add9", new int[] {0, 3, 6, 2}},
            {"maj/min", new int[] {0, 3, 4, 7}},
            {"split3rd", new int[] {0, 3, 4, 7}},
        };

        /// <summary>
        /// Array of chromatic note names used for chord analysis and naming.
        /// </summary>
        private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>
        /// Detects chords from a collection of musical notes using frequency analysis and pattern matching.
        /// </summary>
        /// <param name="notes">The list of detected notes to analyze for chord patterns.</param>
        /// <param name="minOccurrences">Minimum number of times a note must appear to be considered significant. Default is 2.</param>
        /// <param name="maxUniqueNotes">Maximum number of unique notes allowed to filter out noise. Default is 12.</param>
        /// <param name="allowComplexChords">Whether to allow detection of complex chords with 6 or more notes. Default is true.</param>
        /// <returns>A DetectedChord if a valid chord pattern is found, null otherwise.</returns>
        public static DetectedChord? DetectChord(List<DetectedNote> notes, int minOccurrences = 2, int maxUniqueNotes = 12, bool allowComplexChords = true)
        {
            if (notes == null || notes.Count == 0)
                return null;

            var noteFrequency = CountNoteOccurrences(notes);

            var frequentNotes = noteFrequency
                .Where(kvp => kvp.Value >= minOccurrences)
                .OrderByDescending(kvp => kvp.Value)
                .ToList();

            if (frequentNotes.Count < 2 || frequentNotes.Count > maxUniqueNotes)
                return null;

            var chromaticNotes = frequentNotes
                .Select(kvp => GetChromaticNumber(kvp.Key))
                .Where(cn => cn >= 0)
                .Distinct()
                .OrderBy(cn => cn)
                .ToList();

            if (chromaticNotes.Count < 2)
                return null;

            var bestMatch = FindBestChordMatch(chromaticNotes, allowComplexChords);

            if (bestMatch != null)
            {
                return new DetectedChord
                {
                    Notes = bestMatch.Value.notes,
                    ChordName = bestMatch.Value.chordName,
                    Confidence = bestMatch.Value.confidence,
                    OccurrenceCount = frequentNotes.Take(bestMatch.Value.notes.Count).Sum(kvp => kvp.Value)
                };
            }

            return null;
        }

        /// <summary>
        /// Counts the frequency of occurrence for each unique note name in the provided note list.
        /// </summary>
        /// <param name="notes">The list of detected notes to count.</param>
        /// <returns>A dictionary mapping note names to their occurrence counts.</returns>
        private static Dictionary<string, int> CountNoteOccurrences(List<DetectedNote> notes)
        {
            var noteCount = new Dictionary<string, int>();

            foreach (var note in notes)
            {
                string noteName = ExtractNoteName(note.NoteName);

                if (!string.IsNullOrEmpty(noteName))
                {
                    if (noteCount.ContainsKey(noteName))
                        noteCount[noteName]++;
                    else
                        noteCount[noteName] = 1;
                }
            }

            return noteCount;
        }

        /// <summary>
        /// Extracts the note name without octave information from a full note designation.
        /// </summary>
        /// <param name="noteNameWithOctave">The complete note name including octave (e.g., "C4", "F#3").</param>
        /// <returns>The note name without octave (e.g., "C", "F#").</returns>
        private static string ExtractNoteName(string noteNameWithOctave)
        {
            if (string.IsNullOrEmpty(noteNameWithOctave))
                return "";

            string result = "";
            foreach (char c in noteNameWithOctave)
            {
                if (char.IsLetter(c) || c == '#' || c == 'b')
                    result += c;
                else
                    break;
            }

            return result;
        }

        /// <summary>
        /// Converts a note name to its chromatic number representation (0-11 where C=0).
        /// </summary>
        /// <param name="noteName">The note name to convert (supports both sharp and flat notation).</param>
        /// <returns>The chromatic number (0-11) or -1 if the note name is invalid.</returns>
        private static int GetChromaticNumber(string noteName)
        {
            for (int i = 0; i < NoteNames.Length; i++)
            {
                if (NoteNames[i].Equals(noteName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            switch (noteName.ToUpper())
            {
                case "DB": return 1;
                case "EB": return 3;
                case "GB": return 6;
                case "AB": return 8;
                case "BB": return 10;
                default: return -1;
            }
        }

        /// <summary>
        /// Finds the best matching chord pattern for the given set of chromatic notes.
        /// </summary>
        /// <param name="chromaticNotes">The list of chromatic note numbers to match against chord templates.</param>
        /// <param name="allowComplexChords">Whether to include complex chord patterns in the search.</param>
        /// <returns>A tuple containing the matched notes, chord name, and confidence score, or null if no match found.</returns>
        private static (List<string> notes, string chordName, float confidence)? FindBestChordMatch(List<int> chromaticNotes, bool allowComplexChords = true)
        {
            float bestConfidence = 0;
            (List<string> notes, string chordName, float confidence)? bestMatch = null;

            var templatesToCheck = allowComplexChords
                ? ChordTemplates
                : ChordTemplates.Where(kvp => kvp.Value.Length <= 5).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            for (int root = 0; root < 12; root++)
            {
                var orderedTemplates = templatesToCheck
                    .OrderBy(t => t.Value.Length)
                    .ThenBy(t => GetChordComplexity(t.Key));

                foreach (var template in orderedTemplates)
                {
                    var expectedNotes = template.Value.Select(interval => (root + interval) % 12).OrderBy(x => x).ToList();

                    float confidence = CalculateChordConfidence(chromaticNotes, expectedNotes, template.Key);

                    float requiredConfidence = GetRequiredConfidence(template.Key, expectedNotes.Count);

                    if (confidence > bestConfidence && confidence >= requiredConfidence)
                    {
                        bestConfidence = confidence;
                        var chordNotes = expectedNotes.Select(cn => NoteNames[cn]).ToList();
                        string chordName = $"{NoteNames[root]}{template.Key}";

                        bestMatch = (chordNotes, chordName, confidence);
                    }
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Calculates a harmonic complexity score for chord prioritization during pattern matching.
        /// </summary>
        /// <param name="chordType">The chord type identifier to evaluate.</param>
        /// <returns>An integer complexity score where lower values indicate simpler, more common chords.</returns>
        private static int GetChordComplexity(string chordType)
        {
            var complexityMap = new Dictionary<string, int>
            {
                {"major", 1}, {"minor", 1}, {"5", 1},
                {"sus2", 2}, {"sus4", 2}, {"6", 2}, {"m6", 2},
                {"dom7", 3}, {"maj7", 3}, {"min7", 3},
                {"add9", 4}, {"madd9", 4}, {"9", 4},
                {"dim", 5}, {"aug", 5}, {"dim7", 5},
                {"11", 6}, {"13", 6}, {"maj9", 6}, {"min9", 6},
                {"7alt", 8}, {"quartal", 8}, {"so what", 9}, {"cluster2", 10}
            };

            return complexityMap.ContainsKey(chordType) ? complexityMap[chordType] : 7;
        }

        /// <summary>
        /// Determines the required confidence threshold for chord detection based on chord complexity.
        /// </summary>
        /// <param name="chordType">The type of chord being evaluated.</param>
        /// <param name="noteCount">The number of notes in the chord.</param>
        /// <returns>The minimum confidence threshold (0.0 to 1.0) required for detection.</returns>
        private static float GetRequiredConfidence(string chordType, int noteCount)
        {
            if (noteCount <= 3) return 0.75f;
            if (noteCount == 4) return 0.65f;
            if (noteCount == 5) return 0.55f;
            if (noteCount >= 6) return 0.45f;

            if (chordType.Contains("cluster") || chordType.Contains("quartal"))
                return 0.4f;

            return 0.6f;
        }

        /// <summary>
        /// Calculates a confidence score indicating how well detected notes match an expected chord pattern.
        /// </summary>
        /// <param name="detectedNotes">The chromatic numbers of detected notes.</param>
        /// <param name="expectedNotes">The chromatic numbers of the expected chord pattern.</param>
        /// <param name="chordType">The chord type for specialized scoring adjustments.</param>
        /// <returns>A confidence score from 0.0 to 1.0 indicating match quality.</returns>
        private static float CalculateChordConfidence(List<int> detectedNotes, List<int> expectedNotes, string chordType = "")
        {
            if (expectedNotes.Count == 0)
                return 0;

            int matchingNotes = expectedNotes.Count(expected => detectedNotes.Contains(expected));

            float basicConfidence = (float)matchingNotes / expectedNotes.Count;

            float completenessBonus = matchingNotes == expectedNotes.Count ? 0.1f : 0f;

            float specialBonus = 0f;

            if (chordType == "5" && detectedNotes.Count == 2 && matchingNotes == 2)
            {
                specialBonus = 0.2f;
            }

            int extraNotes = Math.Max(0, detectedNotes.Count - expectedNotes.Count);
            float extraNotePenalty;

            if (expectedNotes.Count >= 5)
            {
                extraNotePenalty = Math.Min(0.2f, extraNotes * 0.05f);
            }
            else if (expectedNotes.Count >= 4)
            {
                extraNotePenalty = Math.Min(0.25f, extraNotes * 0.08f);
            }
            else
            {
                extraNotePenalty = Math.Min(0.3f, extraNotes * 0.1f);
            }

            int missingNotes = expectedNotes.Count - matchingNotes;
            float missingNotePenalty = 0f;

            if (missingNotes > 0)
            {
                bool missingRoot = expectedNotes.Contains(0) && !detectedNotes.Contains(0);
                bool missingThird = false;

                foreach (int note in expectedNotes)
                {
                    int interval = note;
                    if ((interval == 3 || interval == 4) && !detectedNotes.Contains(note))
                    {
                        missingThird = true;
                        break;
                    }
                }

                if (missingRoot) missingNotePenalty += 0.3f;
                if (missingThird && !chordType.Contains("sus") && !chordType.Contains("5") && !chordType.Contains("quartal"))
                    missingNotePenalty += 0.2f;

                missingNotePenalty += missingNotes * 0.1f;
            }

            float finalConfidence = Math.Max(0, basicConfidence + completenessBonus + specialBonus - extraNotePenalty - missingNotePenalty);

            return Math.Min(1.0f, finalConfidence);
        }

        /// <summary>
        /// Formats chord names into more readable musical notation by replacing verbose terms with standard symbols.
        /// </summary>
        /// <param name="chordName">The raw chord name to format.</param>
        /// <returns>A formatted chord name using standard musical notation symbols.</returns>
        public static string FormatChordName(string chordName)
        {
            if (string.IsNullOrEmpty(chordName))
                return "";

            return chordName
                .Replace("major", "")
                .Replace("minor", "m")
                .Replace("dim7", "°7")
                .Replace("dim", "°")
                .Replace("half-dim7", "ø7")
                .Replace("aug7", "+7")
                .Replace("aug", "+")
                .Replace("dom7", "7")
                .Replace("maj7", "maj7")
                .Replace("min7", "m7")
                .Replace("minmaj7", "mM7")
                .Replace("sus4", "sus4")
                .Replace("sus2", "sus2")
                .Replace("add9", "add9")
                .Replace("madd9", "madd9")
                .Replace("7sus4", "7sus4")
                .Replace("7sus2", "7sus2")
                .Replace("7b9", "7b9")
                .Replace("7#9", "7#9")
                .Replace("7#11", "7#11")
                .Replace("7b5", "7b5")
                .Replace("7#5", "7#5")
                .Replace("7alt", "7alt")
                .Replace("6/9", "6/9")
                .Replace("m6/9", "m6/9")
                .Replace("maj7#11", "maj7#11")
                .Replace("maj7#5", "maj7#5")
                .Replace("maj7b5", "maj7b5")
                .Replace("min7b5", "m7b5")
                .Replace("maj9#11", "maj9#11")
                .Replace("min9#11", "m9#11")
                .Replace("13sus4", "13sus4")
                .Replace("9sus4", "9sus4")
                .Replace("quartal", "quartal")
                .Replace("so what", "So What")
                .Replace("mu major", "μ")
                .Replace("neapolitan", "N6")
                .Replace("aug6", "Aug6")
                .Replace("fr6", "Fr6")
                .Replace("ger6", "Ger6")
                .Replace("it6", "It6")
                .Replace("split3rd", "split3");
        }
    }
}
