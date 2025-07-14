using System;
using System.Collections.Generic;
using System.Linq;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Utilities.OwnChordDetect.Core
{
    /// <summary>
    /// Represents a musical key with its signature and note naming preferences.
    /// </summary>
    public class MusicalKey
    {
        /// <summary>
        /// Gets the name of the key (e.g., "C", "F#", "Bb").
        /// </summary>
        public string KeyName { get; }

        /// <summary>
        /// Gets a value indicating whether this is a major key (true) or minor key (false).
        /// </summary>
        public bool IsMajor { get; }

        /// <summary>
        /// Gets the number of sharps in the key signature (0-7).
        /// </summary>
        public int Sharps { get; }

        /// <summary>
        /// Gets the number of flats in the key signature (0-7).
        /// </summary>
        public int Flats { get; }

        /// <summary>
        /// Gets the array of preferred note names for this key, determining whether to use sharps or flats.
        /// </summary>
        public string[] PreferredNoteNames { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="MusicalKey"/> class.
        /// </summary>
        /// <param name="keyName">The name of the key (e.g., "C", "F#", "Bb").</param>
        /// <param name="isMajor">True if this is a major key, false if minor.</param>
        /// <param name="sharps">The number of sharps in the key signature (0-7).</param>
        /// <param name="flats">The number of flats in the key signature (0-7).</param>
        /// <param name="preferredNoteNames">The array of preferred note names for this key.</param>
        public MusicalKey(string keyName, bool isMajor, int sharps, int flats, string[] preferredNoteNames)
        {
            KeyName = keyName;
            IsMajor = isMajor;
            Sharps = sharps;
            Flats = flats;
            PreferredNoteNames = preferredNoteNames;
        }

        /// <summary>
        /// Returns a string representation of the musical key in the format "KeyName major/minor".
        /// </summary>
        /// <returns>A string representation of the musical key.</returns>
        public override string ToString() => $"{KeyName} {(IsMajor ? "major" : "minor")}";
    }

    /// <summary>
    /// Detects the musical key of a song using the Krumhansl-Schmuckler algorithm.
    /// </summary>
    public class KeyDetector
    {
        /// <summary>
        /// Krumhansl-Schmuckler major key profile weights for each pitch class.
        /// </summary>
        private static readonly float[] MajorProfile =
        {
            6.35f, 2.23f, 3.48f, 2.33f, 4.38f, 4.09f, 2.52f, 5.19f, 2.39f, 3.66f, 2.29f, 2.88f
        };

        /// <summary>
        /// Krumhansl-Schmuckler minor key profile weights for each pitch class.
        /// </summary>
        private static readonly float[] MinorProfile =
        {
            6.33f, 2.68f, 3.52f, 5.38f, 2.60f, 3.53f, 2.54f, 4.75f, 3.98f, 2.69f, 3.34f, 3.17f
        };

        /// <summary>
        /// Key definitions with note naming preferences, including name, major/minor mode, flat preference, and tonic pitch class.
        /// </summary>
        private static readonly (string name, bool isMajor, bool useFlats, int tonic)[] KeyDefinitions =
        {
            // Major keys with sharps
            ("C", true, false, 0), ("G", true, false, 7), ("D", true, false, 2), ("A", true, false, 9),
            ("E", true, false, 4), ("B", true, false, 11), ("F#", true, false, 6), ("C#", true, false, 1),
            // Major keys with flats
            ("F", true, true, 5), ("Bb", true, true, 10), ("Eb", true, true, 3), ("Ab", true, true, 8),
            ("Db", true, true, 1), ("Gb", true, true, 6), ("Cb", true, true, 11),
            // Minor keys with sharps
            ("Am", false, false, 9), ("Em", false, false, 4), ("Bm", false, false, 11), ("F#m", false, false, 6),
            ("C#m", false, false, 1), ("G#m", false, false, 8), ("D#m", false, false, 3), ("A#m", false, false, 10),
            // Minor keys with flats
            ("Dm", false, true, 2), ("Gm", false, true, 7), ("Cm", false, true, 0), ("Fm", false, true, 5),
            ("Bbm", false, true, 10), ("Ebm", false, true, 3), ("Abm", false, true, 8)
        };

        /// <summary>
        /// Note names using sharp accidentals in chromatic order starting from C.
        /// </summary>
        private static readonly string[] SharpNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>
        /// Note names using flat accidentals in chromatic order starting from C.
        /// </summary>
        private static readonly string[] FlatNames = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

        /// <summary>
        /// Detects the musical key from a list of notes using the Krumhansl-Schmuckler algorithm.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for key detection.</param>
        /// <returns>The detected musical key with the highest correlation score.</returns>
        public MusicalKey DetectKey(List<Note> notes)
        {
            if (!notes.Any())
                return CreateKey("C", true, false, 0);

            var chromagram = ComputeChromagram(notes);
            var bestKey = KeyDefinitions[0];
            var bestCorrelation = float.MinValue;

            foreach (var keyDef in KeyDefinitions)
            {
                var correlation = ComputeKeyCorrelation(chromagram, keyDef);
                if (correlation > bestCorrelation)
                {
                    bestCorrelation = correlation;
                    bestKey = keyDef;
                }
            }

            return CreateKey(bestKey.name, bestKey.isMajor, bestKey.useFlats, bestKey.tonic);
        }

        /// <summary>
        /// Creates a MusicalKey instance with appropriate note naming preferences.
        /// </summary>
        /// <param name="name">The name of the key.</param>
        /// <param name="isMajor">True if this is a major key, false if minor.</param>
        /// <param name="useFlats">True to use flat notation, false to use sharp notation.</param>
        /// <param name="tonic">The tonic pitch class of the key (0-11).</param>
        /// <returns>A new MusicalKey instance with the specified properties.</returns>
        private MusicalKey CreateKey(string name, bool isMajor, bool useFlats, int tonic)
        {
            var noteNames = useFlats ? FlatNames : SharpNames;
            var sharps = useFlats ? 0 : GetSharpCount(name, isMajor);
            var flats = useFlats ? GetFlatCount(name, isMajor) : 0;

            return new MusicalKey(name, isMajor, sharps, flats, noteNames);
        }

        /// <summary>
        /// Computes the correlation between a chromagram and a key profile.
        /// </summary>
        /// <param name="chromagram">The pitch class histogram of the input notes.</param>
        /// <param name="keyDef">The key definition containing name, mode, and tonic information.</param>
        /// <returns>The correlation coefficient between the chromagram and the key profile.</returns>
        private float ComputeKeyCorrelation(float[] chromagram, (string name, bool isMajor, bool useFlats, int tonic) keyDef)
        {
            var profile = keyDef.isMajor ? MajorProfile : MinorProfile;
            var rotatedProfile = RotateProfile(profile, keyDef.tonic);
            return ComputeCorrelation(chromagram, rotatedProfile);
        }

        /// <summary>
        /// Computes a chromagram (pitch class histogram) from a list of notes.
        /// </summary>
        /// <param name="notes">The list of notes to analyze.</param>
        /// <returns>A normalized 12-element array representing the pitch class distribution.</returns>
        private float[] ComputeChromagram(List<Note> notes)
        {
            var chroma = new float[12];

            foreach (var note in notes)
            {
                var pitchClass = note.Pitch % 12;
                var duration = note.EndTime - note.StartTime;
                chroma[pitchClass] += note.Amplitude * duration;
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
        /// Rotates a key profile array to align with a specific tonic pitch class.
        /// </summary>
        /// <param name="profile">The original key profile array.</param>
        /// <param name="steps">The number of semitones to rotate (0-11).</param>
        /// <returns>A rotated copy of the profile array.</returns>
        private float[] RotateProfile(float[] profile, int steps)
        {
            var rotated = new float[12];
            for (int i = 0; i < 12; i++)
            {
                rotated[i] = profile[(i - steps + 12) % 12];
            }
            return rotated;
        }

        /// <summary>
        /// Computes the Pearson correlation coefficient between two arrays.
        /// </summary>
        /// <param name="x">The first array for correlation calculation.</param>
        /// <param name="y">The second array for correlation calculation.</param>
        /// <returns>The Pearson correlation coefficient between the two arrays (-1.0 to 1.0).</returns>
        private float ComputeCorrelation(float[] x, float[] y)
        {
            var n = x.Length;
            var sumX = x.Sum();
            var sumY = y.Sum();
            var sumXY = x.Zip(y, (a, b) => a * b).Sum();
            var sumX2 = x.Sum(a => a * a);
            var sumY2 = y.Sum(a => a * a);

            var numerator = n * sumXY - sumX * sumY;
            var denominator = Math.Sqrt((n * sumX2 - sumX * sumX) * (n * sumY2 - sumY * sumY));

            return denominator > 0 ? (float)(numerator / denominator) : 0;
        }

        /// <summary>
        /// Gets the number of sharps in the key signature for a given key name.
        /// </summary>
        /// <param name="keyName">The name of the key.</param>
        /// <param name="isMajor">True if this is a major key, false if minor.</param>
        /// <returns>The number of sharps in the key signature (0-7).</returns>
        private int GetSharpCount(string keyName, bool isMajor)
        {
            var sharpOrder = new[] { "C", "G", "D", "A", "E", "B", "F#", "C#" };
            var minorSharpOrder = new[] { "Am", "Em", "Bm", "F#m", "C#m", "G#m", "D#m", "A#m" };

            var order = isMajor ? sharpOrder : minorSharpOrder;
            var index = Array.IndexOf(order, keyName);
            return index >= 0 ? index : 0;
        }

        /// <summary>
        /// Gets the number of flats in the key signature for a given key name.
        /// </summary>
        /// <param name="keyName">The name of the key.</param>
        /// <param name="isMajor">True if this is a major key, false if minor.</param>
        /// <returns>The number of flats in the key signature (0-7).</returns>
        private int GetFlatCount(string keyName, bool isMajor)
        {
            var flatOrder = new[] { "C", "F", "Bb", "Eb", "Ab", "Db", "Gb", "Cb" };
            var minorFlatOrder = new[] { "Am", "Dm", "Gm", "Cm", "Fm", "Bbm", "Ebm", "Abm" };

            var order = isMajor ? flatOrder : minorFlatOrder;
            var index = Array.IndexOf(order, keyName);
            return index >= 0 ? index : 0;
        }
    }
}
