using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OwnaudioNET.Features.OwnChordDetect.Evaluation
{
    /// <summary>
    /// One ground-truth segment: a time span, its Harte label and the parsed symbol.
    /// </summary>
    internal sealed class ChordAnnotation
    {
        /// <summary>
        /// Segment start, seconds.
        /// </summary>
        internal float StartTime { get; }

        /// <summary>
        /// Segment end, seconds.
        /// </summary>
        internal float EndTime { get; }

        /// <summary>
        /// The label as it stood in the file.
        /// </summary>
        internal string Label { get; }

        /// <summary>
        /// Same label, parsed.
        /// </summary>
        internal ChordSymbol Symbol { get; }

        /// <summary>
        /// Parses the label right away, so a bad one blows up here and not mid-evaluation.
        /// </summary>
        internal ChordAnnotation(float startTime, float endTime, string label)
        {
            StartTime = startTime;
            EndTime = endTime;
            Label = label;
            Symbol = ChordSymbol.ParseHarte(label);
        }
    }

    /// <summary>
    /// Reads .lab ground truth (Isophonics/Beatles, Billboard): one "start end label" per line,
    /// '#' comments and blank lines skipped.
    /// </summary>
    internal static class LabAnnotationParser
    {
        /// <summary>
        /// Parses .lab text into segments, file order kept.
        /// </summary>
        /// <exception cref="FormatException">A line isn't "start end label".</exception>
        internal static List<ChordAnnotation> Parse(string content)
        {
            var segments = new List<ChordAnnotation>();

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    throw new FormatException($"Invalid .lab line: '{line}'");

                float _start = float.Parse(parts[0], CultureInfo.InvariantCulture);
                float _end = float.Parse(parts[1], CultureInfo.InvariantCulture);

                segments.Add(new ChordAnnotation(_start, _end, parts[2]));
            }

            return segments;
        }

        /// <summary>
        /// Same, straight off disk.
        /// </summary>
        internal static List<ChordAnnotation> ParseFile(string path)
        {
            return Parse(File.ReadAllText(path));
        }
    }
}
