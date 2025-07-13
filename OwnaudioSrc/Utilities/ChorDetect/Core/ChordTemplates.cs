using System.Collections.Generic;

namespace Ownaudio.Utilities.OwnChordDetect.Core
{
    /// <summary>
    /// Manages chord templates and note name conversion.
    /// </summary>
    public static class ChordTemplates
    {
        /// <summary>
        /// Array of note names in chromatic order starting from C.
        /// </summary>
        private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>
        /// Converts a pitch class number to its corresponding note name.
        /// </summary>
        /// <param name="pitchClass">The pitch class number (0-11, where 0=C, 1=C#, etc.)</param>
        /// <returns>The note name as a string (e.g., "C", "F#", "Bb")</returns>
        public static string GetNoteName(int pitchClass) => NoteNames[pitchClass % 12];

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
        /// Creates a dictionary of basic chord templates including major, minor, and 7th chords.
        /// </summary>
        /// <returns>A dictionary mapping chord names to their template arrays for all 12 root notes</returns>
        public static Dictionary<string, float[]> CreateBasicTemplates()
        {
            var templates = new Dictionary<string, float[]>();

            for (int root = 0; root < 12; root++)
            {
                var noteName = GetNoteName(root);

                // Major triads (root, major third, perfect fifth)
                templates[noteName] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12 });

                // Minor triads (root, minor third, perfect fifth)
                templates[noteName + "m"] = CreateTemplate(new[] { root, (root + 3) % 12, (root + 7) % 12 });

                // Dominant 7th (root, major third, perfect fifth, minor seventh)
                templates[noteName + "7"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12, (root + 10) % 12 });

                // Major 7th (root, major third, perfect fifth, major seventh)
                templates[noteName + "maj7"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12, (root + 11) % 12 });

                // Minor 7th (root, minor third, perfect fifth, minor seventh)
                templates[noteName + "m7"] = CreateTemplate(new[] { root, (root + 3) % 12, (root + 7) % 12, (root + 10) % 12 });
            }

            return templates;
        }

        /// <summary>
        /// Creates a dictionary of extended chord templates including suspended, diminished, augmented, and add9 chords.
        /// </summary>
        /// <returns>A dictionary mapping extended chord names to their template arrays for all 12 root notes</returns>
        public static Dictionary<string, float[]> CreateExtendedTemplates()
        {
            var templates = new Dictionary<string, float[]>();

            for (int root = 0; root < 12; root++)
            {
                var noteName = GetNoteName(root);

                // Suspended chords
                // Sus2: root, major second, perfect fifth
                templates[noteName + "sus2"] = CreateTemplate(new[] { root, (root + 2) % 12, (root + 7) % 12 });
                // Sus4: root, perfect fourth, perfect fifth
                templates[noteName + "sus4"] = CreateTemplate(new[] { root, (root + 5) % 12, (root + 7) % 12 });

                // Diminished and augmented
                // Diminished: root, minor third, diminished fifth
                templates[noteName + "dim"] = CreateTemplate(new[] { root, (root + 3) % 12, (root + 6) % 12 });
                // Augmented: root, major third, augmented fifth
                templates[noteName + "aug"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 8) % 12 });

                // Add9: root, major third, perfect fifth, major ninth
                templates[noteName + "add9"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12, (root + 2) % 12 });

                /*
               // 6th chords
               templates[noteName + "6"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12, (root + 9) % 12 });
               templates[noteName + "m6"] = CreateTemplate(new[] { root, (root + 3) % 12, (root + 7) % 12, (root + 9) % 12 });

               // 9th chords
               templates[noteName + "9"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12, (root + 10) % 12, (root + 2) % 12 });
               templates[noteName + "m9"] = CreateTemplate(new[] { root, (root + 3) % 12, (root + 7) % 12, (root + 10) % 12, (root + 2) % 12 });
               templates[noteName + "maj9"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12, (root + 11) % 12, (root + 2) % 12 });

               // 11th chords
               templates[noteName + "11"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12, (root + 10) % 12, (root + 2) % 12, (root + 5) % 12 });
               templates[noteName + "m11"] = CreateTemplate(new[] { root, (root + 3) % 12, (root + 7) % 12, (root + 10) % 12, (root + 2) % 12, (root + 5) % 12 });

               // 13th chords
               templates[noteName + "13"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12, (root + 10) % 12, (root + 2) % 12, (root + 9) % 12 });
               templates[noteName + "m13"] = CreateTemplate(new[] { root, (root + 3) % 12, (root + 7) % 12, (root + 10) % 12, (root + 2) % 12, (root + 9) % 12 });

               // Altered chords
               templates[noteName + "7b5"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 6) % 12, (root + 10) % 12 });
               templates[noteName + "7#5"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 8) % 12, (root + 10) % 12 });
               templates[noteName + "7b9"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12, (root + 10) % 12, (root + 1) % 12 });
               templates[noteName + "7#9"] = CreateTemplate(new[] { root, (root + 4) % 12, (root + 7) % 12, (root + 10) % 12, (root + 3) % 12 });

               // Half-diminished
               templates[noteName + "m7b5"] = CreateTemplate(new[] { root, (root + 3) % 12, (root + 6) % 12, (root + 10) % 12 });

               // Diminished 7th
               templates[noteName + "dim7"] = CreateTemplate(new[] { root, (root + 3) % 12, (root + 6) % 12, (root + 9) % 12 });

               // Add2 variations
               templates[noteName + "madd9"] = CreateTemplate(new[] { root, (root + 3) % 12, (root + 7) % 12, (root + 2) % 12 });
                */
            }

            return templates;
        }
    }
}
