using System;
using System.Collections.Generic;

namespace OwnaudioNET.Features.OwnChordDetect.Evaluation
{
    /// <summary>
    /// Full chord quality, shared by the Harte reference parser and our own name parser.
    /// Gets squashed to a comparison class later. NoChord is Harte "N", Unknown is "X" or
    /// anything we couldn't read, Other is a quality we know but don't have a slot for.
    /// </summary>
    internal enum ChordQuality
    {
        NoChord,
        Unknown,
        Major,
        Minor,
        Dominant7,
        Major7,
        Minor7,
        MajorSixth,
        MinorSixth,
        Sus2,
        Sus4,
        Diminished,
        Augmented,
        Diminished7,
        HalfDiminished7,
        MinorMajor7,
        Dominant9,
        Major9,
        Minor9,
        Add9,
        MinorAdd9,
        Dominant11,
        Minor11,
        Dominant13,
        Minor13,
        Other
    }

    /// <summary>
    /// How coarse the WCSR comparison is, same levels MIREX/mir_eval use. MajMin keeps root plus
    /// major/minor, Sevenths also keeps 7, maj7 and min7. Anything outside drops out of scoring.
    /// </summary>
    internal enum ChordComparisonLevel
    {
        MajMin,
        Sevenths
    }

    /// <summary>
    /// What a chord reduces to at a given level. Excluded means out of vocabulary: skipped as
    /// reference, never matches as estimate.
    /// </summary>
    internal enum ComparisonQuality
    {
        Excluded,
        NoChord,
        Major,
        Minor,
        Dominant7,
        Major7,
        Minor7
    }

    /// <summary>
    /// Root plus quality, parsed from either a Harte label ("A:min7") or one of our own names
    /// ("Am7") so both sides of the evaluation go through the same reduction.
    /// </summary>
    internal readonly struct ChordSymbol
    {
        /// <summary>
        /// 0-11 with C = 0, or -1 for no-chord and unknown.
        /// </summary>
        internal readonly int RootPitchClass;

        /// <summary>
        /// The detailed quality, before any reduction.
        /// </summary>
        internal readonly ChordQuality Quality;

        /// <summary>
        /// Pass -1 as root when the symbol is rootless.
        /// </summary>
        internal ChordSymbol(int rootPitchClass, ChordQuality quality)
        {
            RootPitchClass = rootPitchClass;
            Quality = quality;
        }

        private static readonly Dictionary<string, ChordQuality> _harteShorthands = new()
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

        private static readonly Dictionary<string, ChordQuality> _detectorSuffixes = new()
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
        /// Harte syntax as .lab files use it: root, optional ":shorthand", and the "(intervals)"
        /// and "/bass" parts which we throw away. "N" and "X" are special.
        /// </summary>
        internal static ChordSymbol ParseHarte(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return new ChordSymbol(-1, ChordQuality.Unknown);

            label = label.Trim();

            if (label == "N") return new ChordSymbol(-1, ChordQuality.NoChord);
            if (label == "X") return new ChordSymbol(-1, ChordQuality.Unknown);

            int slash = label.IndexOf('/');
            if (slash >= 0) label = label.Substring(0, slash);

            string rootPart = label;
            string? shorthand = null;

            int colon = label.IndexOf(':');
            if (colon >= 0)
            {
                rootPart = label.Substring(0, colon);
                shorthand = label.Substring(colon + 1);
            }

            int root = _parseRoot(rootPart);
            if (root < 0) return new ChordSymbol(-1, ChordQuality.Unknown);

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

            return _harteShorthands.TryGetValue(shorthand, out var quality)
                ? new ChordSymbol(root, quality)
                : new ChordSymbol(root, ChordQuality.Other);
        }

        /// <summary>
        /// Our own naming ("C", "Am7", "Bbmaj7", "F#dim"). An ambiguity label like "C/C7" keeps
        /// the first alternative only.
        /// </summary>
        internal static ChordSymbol ParseDetectorName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new ChordSymbol(-1, ChordQuality.Unknown);

            name = name.Trim();

            if (name == "N") return new ChordSymbol(-1, ChordQuality.NoChord);
            if (name == "Unknown") return new ChordSymbol(-1, ChordQuality.Unknown);

            int slash = name.IndexOf('/');
            if (slash >= 0) name = name.Substring(0, slash);

            int rootLength = _rootLength(name);
            if (rootLength == 0) return new ChordSymbol(-1, ChordQuality.Unknown);

            int root = _parseRoot(name.Substring(0, rootLength));
            if (root < 0) return new ChordSymbol(-1, ChordQuality.Unknown);

            return _detectorSuffixes.TryGetValue(name.Substring(rootLength), out var quality)
                ? new ChordSymbol(root, quality)
                : new ChordSymbol(root, ChordQuality.Other);
        }

        /// <summary>
        /// Squashes down to the comparison class of the level. Reference and estimate go through
        /// the exact same mapping.
        /// </summary>
        internal ComparisonQuality Reduce(ChordComparisonLevel level)
        {
            if (Quality == ChordQuality.NoChord) return ComparisonQuality.NoChord;
            if (Quality == ChordQuality.Unknown) return ComparisonQuality.Excluded;

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
        /// How many chars the root eats up front — letter plus at most one accidental.
        /// </summary>
        private static int _rootLength(string name)
        {
            if (name.Length == 0 || name[0] < 'A' || name[0] > 'G') return 0;

            return name.Length > 1 && (name[1] == '#' || name[1] == 'b') ? 2 : 1;
        }

        /// <summary>
        /// Note name to pitch class, any number of accidentals. -1 if it isn't a note name.
        /// </summary>
        private static int _parseRoot(string root)
        {
            if (root.Length == 0) return -1;

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

            if (pitchClass < 0) return -1;

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
