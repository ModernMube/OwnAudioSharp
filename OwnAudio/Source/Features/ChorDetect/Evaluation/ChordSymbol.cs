using System;
using System.Collections.Generic;

namespace OwnaudioNET.Features.OwnChordDetect.Evaluation
{
    /// <summary>
    /// Detailed chord quality shared by the Harte-syntax reference parser and the
    /// detector-name parser, later reduced to a comparison vocabulary level.
    /// </summary>
    internal enum ChordQuality
    {
        /// <summary>
        /// Explicit no-chord (silence, noise); Harte "N".
        /// </summary>
        NoChord,

        /// <summary>
        /// Unknown or out-of-vocabulary content; Harte "X" or an unparsable label.
        /// </summary>
        Unknown,

        /// <summary>
        /// Major triad.
        /// </summary>
        Major,

        /// <summary>
        /// Minor triad.
        /// </summary>
        Minor,

        /// <summary>
        /// Dominant seventh chord.
        /// </summary>
        Dominant7,

        /// <summary>
        /// Major seventh chord.
        /// </summary>
        Major7,

        /// <summary>
        /// Minor seventh chord.
        /// </summary>
        Minor7,

        /// <summary>
        /// Major triad with added sixth.
        /// </summary>
        MajorSixth,

        /// <summary>
        /// Minor triad with added sixth.
        /// </summary>
        MinorSixth,

        /// <summary>
        /// Suspended second chord.
        /// </summary>
        Sus2,

        /// <summary>
        /// Suspended fourth chord.
        /// </summary>
        Sus4,

        /// <summary>
        /// Diminished triad.
        /// </summary>
        Diminished,

        /// <summary>
        /// Augmented triad.
        /// </summary>
        Augmented,

        /// <summary>
        /// Diminished seventh chord.
        /// </summary>
        Diminished7,

        /// <summary>
        /// Half-diminished seventh chord.
        /// </summary>
        HalfDiminished7,

        /// <summary>
        /// Minor triad with major seventh.
        /// </summary>
        MinorMajor7,

        /// <summary>
        /// Dominant ninth chord.
        /// </summary>
        Dominant9,

        /// <summary>
        /// Major ninth chord.
        /// </summary>
        Major9,

        /// <summary>
        /// Minor ninth chord.
        /// </summary>
        Minor9,

        /// <summary>
        /// Major triad with added ninth.
        /// </summary>
        Add9,

        /// <summary>
        /// Minor triad with added ninth.
        /// </summary>
        MinorAdd9,

        /// <summary>
        /// Dominant eleventh chord.
        /// </summary>
        Dominant11,

        /// <summary>
        /// Minor eleventh chord.
        /// </summary>
        Minor11,

        /// <summary>
        /// Dominant thirteenth chord.
        /// </summary>
        Dominant13,

        /// <summary>
        /// Minor thirteenth chord.
        /// </summary>
        Minor13,

        /// <summary>
        /// Any other quality outside the recognised set.
        /// </summary>
        Other
    }

    /// <summary>
    /// Vocabulary level of the WCSR comparison, mirroring the standard MIREX/mir_eval levels.
    /// </summary>
    internal enum ChordComparisonLevel
    {
        /// <summary>
        /// Major/minor level: every chord is reduced to its root plus major or minor quality;
        /// references outside the vocabulary (sus, dim, aug) are excluded from scoring.
        /// </summary>
        MajMin,

        /// <summary>
        /// Sevenths level: chords are reduced to root plus one of maj, min, 7, maj7, min7;
        /// references outside the vocabulary are excluded from scoring.
        /// </summary>
        Sevenths
    }

    /// <summary>
    /// Comparison class of a chord after reduction to a vocabulary level.
    /// </summary>
    internal enum ComparisonQuality
    {
        /// <summary>
        /// Outside the vocabulary: excluded from scoring as reference, never matches as estimate.
        /// </summary>
        Excluded,

        /// <summary>
        /// No-chord class.
        /// </summary>
        NoChord,

        /// <summary>
        /// Major class.
        /// </summary>
        Major,

        /// <summary>
        /// Minor class.
        /// </summary>
        Minor,

        /// <summary>
        /// Dominant-seventh class (sevenths level only).
        /// </summary>
        Dominant7,

        /// <summary>
        /// Major-seventh class (sevenths level only).
        /// </summary>
        Major7,

        /// <summary>
        /// Minor-seventh class (sevenths level only).
        /// </summary>
        Minor7
    }

    /// <summary>
    /// A parsed chord symbol: root pitch class plus detailed quality. Produced from either
    /// a Harte-syntax reference label ("A:min7") or an OwnAudio detector name ("Am7"),
    /// so both sides of the evaluation reduce through the same vocabulary mapping.
    /// </summary>
    internal readonly struct ChordSymbol
    {
        /// <summary>
        /// The root pitch class (0-11, C = 0), or -1 for no-chord and unknown symbols.
        /// </summary>
        internal readonly int RootPitchClass;

        /// <summary>
        /// The detailed chord quality.
        /// </summary>
        internal readonly ChordQuality Quality;

        /// <summary>
        /// Initializes a new chord symbol.
        /// </summary>
        /// <param name="rootPitchClass">The root pitch class (0-11), or -1 when rootless.</param>
        /// <param name="quality">The detailed chord quality.</param>
        internal ChordSymbol(int rootPitchClass, ChordQuality quality)
        {
            RootPitchClass = rootPitchClass;
            Quality = quality;
        }

        /// <summary>
        /// Maps Harte shorthand strings to detailed qualities.
        /// </summary>
        private static readonly Dictionary<string, ChordQuality> HarteShorthands = new()
        {
            ["maj"] = ChordQuality.Major,
            ["min"] = ChordQuality.Minor,
            ["7"] = ChordQuality.Dominant7,
            ["maj7"] = ChordQuality.Major7,
            ["min7"] = ChordQuality.Minor7,
            ["maj6"] = ChordQuality.MajorSixth,
            ["6"] = ChordQuality.MajorSixth,
            ["min6"] = ChordQuality.MinorSixth,
            ["sus2"] = ChordQuality.Sus2,
            ["sus4"] = ChordQuality.Sus4,
            ["dim"] = ChordQuality.Diminished,
            ["aug"] = ChordQuality.Augmented,
            ["dim7"] = ChordQuality.Diminished7,
            ["hdim"] = ChordQuality.HalfDiminished7,
            ["hdim7"] = ChordQuality.HalfDiminished7,
            ["minmaj7"] = ChordQuality.MinorMajor7,
            ["9"] = ChordQuality.Dominant9,
            ["maj9"] = ChordQuality.Major9,
            ["min9"] = ChordQuality.Minor9,
            ["add9"] = ChordQuality.Add9,
            ["11"] = ChordQuality.Dominant11,
            ["min11"] = ChordQuality.Minor11,
            ["13"] = ChordQuality.Dominant13,
            ["min13"] = ChordQuality.Minor13,
        };

        /// <summary>
        /// Maps OwnAudio detector name suffixes to detailed qualities.
        /// </summary>
        private static readonly Dictionary<string, ChordQuality> DetectorSuffixes = new()
        {
            [""] = ChordQuality.Major,
            ["m"] = ChordQuality.Minor,
            ["7"] = ChordQuality.Dominant7,
            ["maj7"] = ChordQuality.Major7,
            ["m7"] = ChordQuality.Minor7,
            ["6"] = ChordQuality.MajorSixth,
            ["m6"] = ChordQuality.MinorSixth,
            ["sus2"] = ChordQuality.Sus2,
            ["sus4"] = ChordQuality.Sus4,
            ["dim"] = ChordQuality.Diminished,
            ["aug"] = ChordQuality.Augmented,
            ["dim7"] = ChordQuality.Diminished7,
            ["m7b5"] = ChordQuality.HalfDiminished7,
            ["9"] = ChordQuality.Dominant9,
            ["maj9"] = ChordQuality.Major9,
            ["m9"] = ChordQuality.Minor9,
            ["add9"] = ChordQuality.Add9,
            ["madd9"] = ChordQuality.MinorAdd9,
            ["11"] = ChordQuality.Dominant11,
            ["m11"] = ChordQuality.Minor11,
            ["13"] = ChordQuality.Dominant13,
            ["m13"] = ChordQuality.Minor13,
            ["7b5"] = ChordQuality.Dominant7,
            ["7#5"] = ChordQuality.Dominant7,
            ["7#9"] = ChordQuality.Dominant7,
        };

        /// <summary>
        /// Parses a chord label in Harte syntax (the standard of .lab reference annotations):
        /// root with accidentals, optional ":shorthand", optional "(intervals)" and "/bass"
        /// parts (both ignored), plus the special labels "N" (no chord) and "X" (unknown).
        /// </summary>
        /// <param name="label">The Harte chord label (e.g. "A:min7", "Db", "N").</param>
        /// <returns>The parsed chord symbol; unparsable labels yield <see cref="ChordQuality.Unknown"/>.</returns>
        internal static ChordSymbol ParseHarte(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return new ChordSymbol(-1, ChordQuality.Unknown);

            label = label.Trim();

            if (label == "N")
                return new ChordSymbol(-1, ChordQuality.NoChord);
            if (label == "X")
                return new ChordSymbol(-1, ChordQuality.Unknown);

            int slash = label.IndexOf('/');
            if (slash >= 0)
                label = label.Substring(0, slash);

            string rootPart = label;
            string? shorthand = null;

            int colon = label.IndexOf(':');
            if (colon >= 0)
            {
                rootPart = label.Substring(0, colon);
                shorthand = label.Substring(colon + 1);
            }

            int root = ParseRoot(rootPart);
            if (root < 0)
                return new ChordSymbol(-1, ChordQuality.Unknown);

            if (shorthand == null || shorthand.Length == 0)
                return new ChordSymbol(root, ChordQuality.Major);

            bool hadIntervalList = false;
            int parenthesis = shorthand.IndexOf('(');
            if (parenthesis >= 0)
            {
                hadIntervalList = true;
                shorthand = shorthand.Substring(0, parenthesis);
            }

            if (shorthand.Length == 0)
                return new ChordSymbol(root, hadIntervalList ? ChordQuality.Other : ChordQuality.Major);

            return HarteShorthands.TryGetValue(shorthand, out var quality)
                ? new ChordSymbol(root, quality)
                : new ChordSymbol(root, ChordQuality.Other);
        }

        /// <summary>
        /// Parses an OwnAudio detector chord name (e.g. "C", "Am7", "Bbmaj7", "F#dim").
        /// Combined ambiguity labels ("C/C7") reduce to their first alternative; "N" and
        /// "Unknown" map to their dedicated qualities.
        /// </summary>
        /// <param name="name">The detector chord name.</param>
        /// <returns>The parsed chord symbol; unparsable names yield <see cref="ChordQuality.Unknown"/>.</returns>
        internal static ChordSymbol ParseDetectorName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new ChordSymbol(-1, ChordQuality.Unknown);

            name = name.Trim();

            if (name == "N")
                return new ChordSymbol(-1, ChordQuality.NoChord);
            if (name == "Unknown")
                return new ChordSymbol(-1, ChordQuality.Unknown);

            int slash = name.IndexOf('/');
            if (slash >= 0)
                name = name.Substring(0, slash);

            int rootLength = RootLength(name);
            if (rootLength == 0)
                return new ChordSymbol(-1, ChordQuality.Unknown);

            int root = ParseRoot(name.Substring(0, rootLength));
            if (root < 0)
                return new ChordSymbol(-1, ChordQuality.Unknown);

            string suffix = name.Substring(rootLength);

            return DetectorSuffixes.TryGetValue(suffix, out var quality)
                ? new ChordSymbol(root, quality)
                : new ChordSymbol(root, ChordQuality.Other);
        }

        /// <summary>
        /// Reduces the symbol to its comparison class at the given vocabulary level.
        /// The same reduction applies to reference and estimate: references reduced to
        /// <see cref="ComparisonQuality.Excluded"/> are skipped from scoring, while excluded
        /// estimates simply never match.
        /// </summary>
        /// <param name="level">The comparison vocabulary level.</param>
        /// <returns>The comparison class of the symbol.</returns>
        internal ComparisonQuality Reduce(ChordComparisonLevel level)
        {
            if (Quality == ChordQuality.NoChord)
                return ComparisonQuality.NoChord;
            if (Quality == ChordQuality.Unknown)
                return ComparisonQuality.Excluded;

            if (level == ChordComparisonLevel.MajMin)
            {
                return Quality switch
                {
                    ChordQuality.Major or ChordQuality.Dominant7 or ChordQuality.Major7 or
                    ChordQuality.MajorSixth or ChordQuality.Dominant9 or ChordQuality.Major9 or
                    ChordQuality.Add9 or ChordQuality.Dominant11 or ChordQuality.Dominant13
                        => ComparisonQuality.Major,

                    ChordQuality.Minor or ChordQuality.Minor7 or ChordQuality.MinorSixth or
                    ChordQuality.Minor9 or ChordQuality.MinorAdd9 or ChordQuality.Minor11 or
                    ChordQuality.Minor13 or ChordQuality.MinorMajor7
                        => ComparisonQuality.Minor,

                    _ => ComparisonQuality.Excluded
                };
            }

            return Quality switch
            {
                ChordQuality.Major or ChordQuality.Add9 or ChordQuality.MajorSixth
                    => ComparisonQuality.Major,

                ChordQuality.Minor or ChordQuality.MinorAdd9 or ChordQuality.MinorSixth
                    => ComparisonQuality.Minor,

                ChordQuality.Dominant7 or ChordQuality.Dominant9 or
                ChordQuality.Dominant11 or ChordQuality.Dominant13
                    => ComparisonQuality.Dominant7,

                ChordQuality.Major7 or ChordQuality.Major9
                    => ComparisonQuality.Major7,

                ChordQuality.Minor7 or ChordQuality.Minor9 or
                ChordQuality.Minor11 or ChordQuality.Minor13
                    => ComparisonQuality.Minor7,

                _ => ComparisonQuality.Excluded
            };
        }

        /// <summary>
        /// Returns the length of the root portion (letter plus one optional accidental)
        /// at the start of a detector chord name.
        /// </summary>
        /// <param name="name">The detector chord name.</param>
        /// <returns>The number of characters forming the root, or 0 when there is no root letter.</returns>
        private static int RootLength(string name)
        {
            if (name.Length == 0 || name[0] < 'A' || name[0] > 'G')
                return 0;

            return name.Length > 1 && (name[1] == '#' || name[1] == 'b') ? 2 : 1;
        }

        /// <summary>
        /// Parses a root note name (letter plus any number of accidentals) to a pitch class.
        /// </summary>
        /// <param name="root">The root name (e.g. "C", "F#", "Bbb").</param>
        /// <returns>The pitch class (0-11), or -1 when the name is invalid.</returns>
        private static int ParseRoot(string root)
        {
            if (root.Length == 0)
                return -1;

            int pitchClass = root[0] switch
            {
                'C' => 0,
                'D' => 2,
                'E' => 4,
                'F' => 5,
                'G' => 7,
                'A' => 9,
                'B' => 11,
                _ => -1
            };

            if (pitchClass < 0)
                return -1;

            for (int i = 1; i < root.Length; i++)
            {
                if (root[i] == '#')
                    pitchClass++;
                else if (root[i] == 'b')
                    pitchClass--;
                else
                    return -1;
            }

            return ((pitchClass % 12) + 12) % 12;
        }
    }
}
