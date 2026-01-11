using System.Collections.Generic;
using System.Linq;

namespace OwnaudioNET.Features.OwnChordDetect.Core
{
    /// <summary>
    /// Manages chord templates and note name conversion with key-aware naming.
    /// </summary>
    public static class ChordTemplates
    {
        /// <summary>
        /// Array of note names in chromatic order starting from C (default with sharps).
        /// </summary>
        private static readonly string[] DefaultNoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>
        /// Converts a pitch class number to its corresponding note name using the specified key context.
        /// </summary>
        /// <param name="pitchClass">The pitch class number (0-11, where 0=C, 1=C#, etc.)</param>
        /// <param name="key">The musical key context for appropriate note naming. If null, uses default sharp notation.</param>
        /// <returns>The note name as a string (e.g., "C", "F#", "Bb")</returns>
        public static string GetNoteName(int pitchClass, MusicalKey? key = null)
        {
            return key?.PreferredNoteNames[pitchClass % 12] ?? DefaultNoteNames[pitchClass % 12];
        }

        /// <summary>
        /// Creates a chord template array from an array of pitch classes.
        /// </summary>
        /// <param name="pitchClasses">Array of pitch class numbers that form the chord</param>
        /// <returns>A 12-element float array representing the chord template with equal weights</returns>
        public static float[] CreateTemplate(int[] pitchClasses)
        {
            var template = new float[12];
            var weight = 1.0f / pitchClasses.Length;

            foreach (var pc in pitchClasses)
            {
                template[pc % 12] = weight;
            }

            return template;
        }

        /// <summary>
        /// Creates all chord templates with key-aware naming.
        /// </summary>
        /// <param name="key">The musical key context for appropriate chord naming</param>
        /// <param name="includeExtended">Whether to include extended chords (9th, 11th, 13th)</param>
        /// <returns>A dictionary mapping chord names to their template arrays</returns>
        public static Dictionary<string, float[]> CreateAllTemplates(MusicalKey? key = null, bool includeExtended = true)
        {
            var templates = new Dictionary<string, float[]>();
            var chordDefinitions = includeExtended ? GetAllChordDefinitions() : GetBasicChordDefinitions();

            for (int root = 0; root < 12; root++)
            {
                var noteName = GetNoteName(root, key);

                foreach (var (suffix, intervals) in chordDefinitions)
                {
                    var pitchClasses = intervals.Select(interval => (root + interval) % 12).ToArray();
                    templates[noteName + suffix] = CreateTemplate(pitchClasses);
                }
            }

            return templates;
        }

        /// <summary>
        /// Gets basic chord definitions (triads and 7th chords).
        /// </summary>
        /// <returns>An array of tuples containing chord suffixes and their corresponding interval patterns</returns>
        private static (string suffix, int[] intervals)[] GetBasicChordDefinitions() => new[]
        {
            ("", new[] { 0, 4, 7 }),           // Major
            ("m", new[] { 0, 3, 7 }),          // Minor
            ("7", new[] { 0, 4, 7, 10 }),      // Dominant 7th
            ("maj7", new[] { 0, 4, 7, 11 }),   // Major 7th
            ("m7", new[] { 0, 3, 7, 10 })      // Minor 7th
        };

        /// <summary>
        /// Gets all chord definitions including extended chords.
        /// </summary>
        /// <returns>An array of tuples containing chord suffixes and their corresponding interval patterns for all chord types</returns>
        private static (string suffix, int[] intervals)[] GetAllChordDefinitions() => new[]
        {
            ("", new[] { 0, 4, 7 }),           // Major
            ("m", new[] { 0, 3, 7 }),          // Minor
            ("7", new[] { 0, 4, 7, 10 }),      // Dominant 7th
            ("maj7", new[] { 0, 4, 7, 11 }),   // Major 7th
            ("m7", new[] { 0, 3, 7, 10 }),     // Minor 7th
            ("sus2", new[] { 0, 2, 7 }),       // Sus2
            ("sus4", new[] { 0, 5, 7 }),       // Sus4
            ("dim", new[] { 0, 3, 6 }),        // Diminished
            ("aug", new[] { 0, 4, 8 }),        // Augmented
            ("add9", new[] { 0, 4, 7, 2 }),    // Add9
            ("6", new[] { 0, 4, 7, 9 }),       // 6th
            ("m6", new[] { 0, 3, 7, 9 }),      // Minor 6th
            ("9", new[] { 0, 4, 7, 10, 2 }),   // 9th
            ("m9", new[] { 0, 3, 7, 10, 2 }),  // Minor 9th
            ("maj9", new[] { 0, 4, 7, 11, 2 }), // Major 9th
            ("11", new[] { 0, 4, 7, 10, 2, 5 }), // 11th
            ("m11", new[] { 0, 3, 7, 10, 2, 5 }), // Minor 11th
            ("13", new[] { 0, 4, 7, 10, 2, 9 }), // 13th
            ("m13", new[] { 0, 3, 7, 10, 2, 9 }), // Minor 13th

            /* Uncomment for extended chord definitions */
            ("7b5", new[] { 0, 4, 6, 10 }),   // Altered chords
            ("7#5", new[] { 0, 4, 8, 10 }),
            ("7#9", new[] { 0, 4, 7, 10, 3 }),
            ("m7b5", new[] { 0, 3, 6, 10 }),    // Half-diminished
            ("dim7", new[] { 0, 3, 6, 9 }),     // Diminished 7th
            ("madd9", new[] { 0, 3, 7, 2 })    // Add2 variations           
        };
    }
}
