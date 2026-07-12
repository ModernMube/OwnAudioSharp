using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OwnaudioNET.Features.OwnChordDetect.Evaluation
{
    /// <summary>
    /// One reference chord segment of a ground-truth annotation: a time span with its
    /// Harte-syntax label and the parsed chord symbol.
    /// </summary>
    internal sealed class ChordAnnotation
    {
        /// <summary>
        /// The start time of the segment in seconds.
        /// </summary>
        internal float StartTime { get; }

        /// <summary>
        /// The end time of the segment in seconds.
        /// </summary>
        internal float EndTime { get; }

        /// <summary>
        /// The original Harte-syntax chord label of the segment.
        /// </summary>
        internal string Label { get; }

        /// <summary>
        /// The parsed chord symbol of the segment.
        /// </summary>
        internal ChordSymbol Symbol { get; }

        /// <summary>
        /// Initializes a new reference chord segment.
        /// </summary>
        /// <param name="startTime">The start time in seconds.</param>
        /// <param name="endTime">The end time in seconds.</param>
        /// <param name="label">The Harte-syntax chord label.</param>
        internal ChordAnnotation(float startTime, float endTime, string label)
        {
            StartTime = startTime;
            EndTime = endTime;
            Label = label;
            Symbol = ChordSymbol.ParseHarte(label);
        }
    }

    /// <summary>
    /// Parses ground-truth chord annotations in the standard .lab format used by the
    /// Isophonics/Beatles and Billboard corpora: one segment per line as
    /// "start&#160;end&#160;label" with whitespace separators, '#' comments and blank lines allowed.
    /// </summary>
    internal static class LabAnnotationParser
    {
        /// <summary>
        /// Parses .lab content into chronological reference segments.
        /// </summary>
        /// <param name="content">The full text of a .lab annotation.</param>
        /// <returns>The parsed segments in file order.</returns>
        /// <exception cref="FormatException">A non-comment line does not follow the "start end label" form.</exception>
        internal static List<ChordAnnotation> Parse(string content)
        {
            var segments = new List<ChordAnnotation>();

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    throw new FormatException($"Invalid .lab line: '{line}'");

                float start = float.Parse(parts[0], CultureInfo.InvariantCulture);
                float end = float.Parse(parts[1], CultureInfo.InvariantCulture);

                segments.Add(new ChordAnnotation(start, end, parts[2]));
            }

            return segments;
        }

        /// <summary>
        /// Reads and parses a .lab annotation file.
        /// </summary>
        /// <param name="path">The path of the .lab file.</param>
        /// <returns>The parsed segments in file order.</returns>
        internal static List<ChordAnnotation> ParseFile(string path)
        {
            return Parse(File.ReadAllText(path));
        }
    }
}
