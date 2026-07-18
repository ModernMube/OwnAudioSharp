using System.Collections.Generic;

namespace OwnaudioNET.Features.OwnChordDetect.Core
{
    /// <summary>
    /// Builds the chord templates we match chromagrams against, and spells note names for a key.
    /// </summary>
    public static class ChordTemplates
    {
        private static readonly string[] _defaultNoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>
        /// Pitch class 0-11 to a note name. Without a key we spell everything with sharps.
        /// </summary>
        public static string GetNoteName(int pitchClass, MusicalKey? key = null)
        {
            return key?.PreferredNoteNames[pitchClass % 12] ?? _defaultNoteNames[pitchClass % 12];
        }

        /// <summary>
        /// Weight per chord-tone slot: root, third, fifth, then extensions. Krumhansl-Kessler probe-tone
        /// ordering — the root carries the identity, the third the quality. Cosine normalizes later,
        /// so only the ratios matter.
        /// </summary>
        private static readonly float[] _toneWeights = { 1.0f, 0.85f, 0.65f, 0.45f, 0.30f, 0.20f };

        /// <summary>
        /// Turns pitch classes into a 12 bin template. Order matters: root first, then third, fifth, extensions.
        /// </summary>
        public static float[] CreateTemplate(int[] pitchClasses)
        {
            var template = new float[12];

            for (int i = 0; i < pitchClasses.Length; i++)
            {
                float weight = i < _toneWeights.Length ? _toneWeights[i] : _toneWeights[_toneWeights.Length - 1];
                template[pitchClasses[i] % 12] = weight;
            }

            return template;
        }

        /// <summary>
        /// Every chord type on every root, named for the key.
        /// includeExtended pulls in the 9th/11th/13th and altered stuff too.
        /// </summary>
        public static Dictionary<string, float[]> CreateAllTemplates(MusicalKey? key = null, bool includeExtended = true)
        {
            var templates = new Dictionary<string, float[]>();
            var chordDefinitions = includeExtended ? _allChordDefs() : _basicChordDefs();

            for (int root = 0; root < 12; root++)
            {
                var noteName = GetNoteName(root, key);

                foreach (var (suffix, intervals) in chordDefinitions)
                {
                    var pitchClasses = new int[intervals.Length];
                    for (int i = 0; i < intervals.Length; i++)
                        pitchClasses[i] = (root + intervals[i]) % 12;

                    templates[noteName + suffix] = CreateTemplate(pitchClasses);
                }
            }

            return templates;
        }

        private static (string suffix, int[] intervals)[] _basicChordDefs() => new[]
        {
            ("", new[] { 0, 4, 7 }),
            ("m", new[] { 0, 3, 7 }),
            ("7", new[] { 0, 4, 7, 10 }),
            ("maj7", new[] { 0, 4, 7, 11 }),
            ("m7", new[] { 0, 3, 7, 10 })
        };

        private static (string suffix, int[] intervals)[] _allChordDefs() => new[]
        {
            ("", new[] { 0, 4, 7 }),
            ("m", new[] { 0, 3, 7 }),
            ("7", new[] { 0, 4, 7, 10 }),
            ("maj7", new[] { 0, 4, 7, 11 }),
            ("m7", new[] { 0, 3, 7, 10 }),
            ("sus2", new[] { 0, 2, 7 }),
            ("sus4", new[] { 0, 5, 7 }),
            ("dim", new[] { 0, 3, 6 }),
            ("aug", new[] { 0, 4, 8 }),
            ("add9", new[] { 0, 4, 7, 2 }),
            ("6", new[] { 0, 4, 7, 9 }),
            ("m6", new[] { 0, 3, 7, 9 }),
            ("9", new[] { 0, 4, 7, 10, 2 }),
            ("m9", new[] { 0, 3, 7, 10, 2 }),
            ("maj9", new[] { 0, 4, 7, 11, 2 }),
            ("11", new[] { 0, 4, 7, 10, 2, 5 }),
            ("m11", new[] { 0, 3, 7, 10, 2, 5 }),
            ("13", new[] { 0, 4, 7, 10, 2, 9 }),
            ("m13", new[] { 0, 3, 7, 10, 2, 9 }),

            ("7b5", new[] { 0, 4, 6, 10 }),
            ("7#5", new[] { 0, 4, 8, 10 }),
            ("7#9", new[] { 0, 4, 7, 10, 3 }),
            ("m7b5", new[] { 0, 3, 6, 10 }),
            ("dim7", new[] { 0, 3, 6, 9 }),
            ("madd9", new[] { 0, 3, 7, 2 })
        };
    }
}
