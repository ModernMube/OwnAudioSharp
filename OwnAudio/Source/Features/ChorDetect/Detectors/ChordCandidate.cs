namespace OwnaudioNET.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// One chord guess for a window. Keeps the raw cosine for honest confidence numbers
    /// and the tweaked score for ranking / Viterbi.
    /// </summary>
    internal readonly struct ChordCandidate
    {
        /// <summary>
        /// Chord name, e.g. "Cmaj7".
        /// </summary>
        internal readonly string Name;

        /// <summary>
        /// Plain cosine between window chroma and template.
        /// </summary>
        internal readonly float Cosine;

        /// <summary>
        /// Cosine after the parsimony / diatonic / bass-root priors.
        /// </summary>
        internal readonly float Score;

        /// <summary>
        /// Root as pitch class 0-11, the decoder needs it for circle-of-fifths distance.
        /// </summary>
        internal readonly int RootPitchClass;

        /// <summary>
        /// All four fields at once.
        /// </summary>
        internal ChordCandidate(string name, float cosine, float score, int rootPitchClass)
        {
            Name = name;
            Cosine = cosine;
            Score = score;
            RootPitchClass = rootPitchClass;
        }
    }
}
