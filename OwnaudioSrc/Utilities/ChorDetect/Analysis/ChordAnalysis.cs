using System.Linq;

namespace Ownaudio.Utilities.OwnChordDetect.Analysis
{
    /// <summary>
    /// Detailed chord analysis result.
    /// </summary>
    public class ChordAnalysis
    {
        /// <summary>
        /// Gets the name of the detected chord.
        /// </summary>
        public string ChordName { get; }

        /// <summary>
        /// Gets the confidence level of the chord detection (0.0 to 1.0).
        /// </summary>
        public float Confidence { get; }

        /// <summary>
        /// Gets the detailed explanation of the chord analysis.
        /// </summary>
        public string Explanation { get; }

        /// <summary>
        /// Gets the array of note names that form the chord.
        /// </summary>
        public string[] NoteNames { get; }

        /// <summary>
        /// Gets or sets a value indicating whether the chord detection is ambiguous.
        /// </summary>
        public bool IsAmbiguous { get; set; }

        /// <summary>
        /// Gets or sets the alternative chord names if the detection is ambiguous.
        /// </summary>
        public string[] Alternatives { get; set; } = new string[0];

        /// <summary>
        /// Gets or sets the pitch classes used in the chord analysis.
        /// </summary>
        public int[] PitchClasses { get; set; } = new int[0];

        /// <summary>
        /// Gets or sets the chromagram data used for chord analysis.
        /// </summary>
        public float[] Chromagram { get; set; } = new float[0];

        /// <summary>
        /// Initializes a new instance of the <see cref="ChordAnalysis"/> class.
        /// </summary>
        /// <param name="chordName">The name of the detected chord.</param>
        /// <param name="confidence">The confidence level of the detection (0.0 to 1.0).</param>
        /// <param name="explanation">The detailed explanation of the analysis.</param>
        /// <param name="noteNames">The array of note names that form the chord.</param>
        public ChordAnalysis(string chordName, float confidence, string explanation, string[] noteNames)
        {
            ChordName = chordName;
            Confidence = confidence;
            Explanation = explanation;
            NoteNames = noteNames;
        }

        /// <summary>
        /// Returns a string representation of the chord analysis including alternatives if ambiguous.
        /// </summary>
        /// <returns>A formatted string containing chord name, confidence, explanation, and alternatives.</returns>
        public override string ToString()
        {
            var result = $"{ChordName} (confidence: {Confidence:F3})\n{Explanation}";

            if (IsAmbiguous && Alternatives.Any())
            {
                result += $"\nAlternatives: {string.Join(", ", Alternatives)}";
            }

            return result;
        }
    }
}
