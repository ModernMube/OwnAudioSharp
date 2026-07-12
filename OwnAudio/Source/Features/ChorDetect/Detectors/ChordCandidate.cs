namespace OwnaudioNET.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// A ranked chord hypothesis for one analysis window, produced by
    /// <see cref="ChordDetector.GetChordCandidates"/> and consumed by the Viterbi
    /// progression decoder. Carries both the raw cosine similarity (for unbiased
    /// confidence reporting) and the perceptual score (for ranking and decoding).
    /// </summary>
    internal readonly struct ChordCandidate
    {
        /// <summary>
        /// The chord name this candidate represents (e.g. "Cmaj7").
        /// </summary>
        internal readonly string Name;

        /// <summary>
        /// The raw cosine similarity between the window chromagram and the chord template.
        /// </summary>
        internal readonly float Cosine;

        /// <summary>
        /// The perceptual ranking score (cosine adjusted by parsimony, diatonic and bass-root priors).
        /// </summary>
        internal readonly float Score;

        /// <summary>
        /// The root pitch class of the chord (0-11), used for musically weighted transitions
        /// (circle-of-fifths distance) in the progression decoder.
        /// </summary>
        internal readonly int RootPitchClass;

        /// <summary>
        /// Initializes a new chord candidate.
        /// </summary>
        /// <param name="name">The chord name.</param>
        /// <param name="cosine">The raw cosine similarity.</param>
        /// <param name="score">The perceptual ranking score.</param>
        /// <param name="rootPitchClass">The root pitch class of the chord (0-11).</param>
        internal ChordCandidate(string name, float cosine, float score, int rootPitchClass)
        {
            Name = name;
            Cosine = cosine;
            Score = score;
            RootPitchClass = rootPitchClass;
        }
    }
}
