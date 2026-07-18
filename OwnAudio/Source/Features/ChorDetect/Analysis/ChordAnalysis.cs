using System.Linq;

namespace OwnaudioNET.Features.OwnChordDetect.Analysis
{
    /// <summary>
    /// Everything we figured out about one chord: the call itself, how sure we are, and the raw data behind it.
    /// </summary>
    public class ChordAnalysis
    {
        /// <summary>
        /// The chord we settled on.
        /// </summary>
        public string ChordName { get; }

        /// <summary>
        /// 0..1, how well the template matched.
        /// </summary>
        public float Confidence { get; }

        /// <summary>
        /// Human readable reasoning.
        /// </summary>
        public string Explanation { get; }

        /// <summary>
        /// The notes making up the chord, already spelled for the key.
        /// </summary>
        public string[] NoteNames { get; }

        /// <summary>
        /// True when a runner-up came uncomfortably close.
        /// </summary>
        public bool IsAmbiguous { get; set; }

        /// <summary>
        /// Those runner-ups, if any.
        /// </summary>
        public string[] Alternatives { get; set; } = new string[0];

        /// <summary>
        /// Pitch classes we fed the matcher.
        /// </summary>
        public int[] PitchClasses { get; set; } = new int[0];

        /// <summary>
        /// The 12 bin chromagram this came from.
        /// </summary>
        public float[] Chromagram { get; set; } = new float[0];

        /// <summary>
        /// The read-only half gets set here, the rest is filled in by the detector afterwards.
        /// </summary>
        public ChordAnalysis(string chordName, float confidence, string explanation, string[] noteNames)
        {
            ChordName = chordName;
            Confidence = confidence;
            Explanation = explanation;
            NoteNames = noteNames;
        }

        /// <summary>
        /// Name, confidence and explanation, plus the alternatives when it's a close call.
        /// </summary>
        public override string ToString()
        {
            var result = $"{ChordName} (confidence: {Confidence:F3})\n{Explanation}";

            if (IsAmbiguous && Alternatives.Any())
                result += $"\nAlternatives: {string.Join(", ", Alternatives)}";

            return result;
        }
    }
}
