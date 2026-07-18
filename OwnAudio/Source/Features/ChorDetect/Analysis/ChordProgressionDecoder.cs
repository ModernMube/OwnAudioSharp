using System;
using System.Collections.Generic;
using OwnaudioNET.Features.OwnChordDetect.Detectors;

namespace OwnaudioNET.Features.OwnChordDetect.Analysis
{
    /// <summary>
    /// One slice of the lattice: a time span plus its ranked guesses. Empty candidates means
    /// the window can only end up as no-chord.
    /// </summary>
    internal readonly struct ChordWindow
    {
        /// <summary>
        /// Window start, seconds.
        /// </summary>
        internal readonly float StartTime;

        /// <summary>
        /// Window end, seconds.
        /// </summary>
        internal readonly float EndTime;

        /// <summary>
        /// Best first.
        /// </summary>
        internal readonly ChordCandidate[] Candidates;

        /// <summary>
        /// Straight assignment.
        /// </summary>
        internal ChordWindow(float startTime, float endTime, ChordCandidate[] candidates)
        {
            StartTime = startTime;
            EndTime = endTime;
            Candidates = candidates;
        }
    }

    /// <summary>
    /// Picks the best chord path across the windows with Viterbi — the usual temporal smoothing
    /// of chord estimation systems (Sheh &amp; Ellis 2003, Bello &amp; Pickens 2005).
    /// <para>
    /// Windows don't vote on their own: staying put is free, changing costs, and the cost grows
    /// with circle-of-fifths distance. A real chord change keeps its advantage over many windows
    /// and still wins, single window flicker doesn't. A no-chord state soaks up the windows where
    /// nothing convincing happens.
    /// </para>
    /// </summary>
    internal sealed class ChordProgressionDecoder
    {
        /// <summary>
        /// Flat cost of switching chords. Sized above what an extension relabel (F to Fadd9)
        /// can gain on a transition window, so root-preserving flicker dies too.
        /// </summary>
        private const float ChordChangePenalty = 0.10f;

        /// <summary>
        /// Multiplied by the 0-6 fifths distance between the two roots. Progressions mostly
        /// move between close harmonies.
        /// </summary>
        private const float FifthsDistanceWeight = 0.008f;

        /// <summary>
        /// Cost of stepping in or out of no-chord. Deliberately cheaper than a chord-to-chord
        /// jump, silence boundaries shouldn't be expensive.
        /// </summary>
        private const float NoChordTransitionPenalty = 0.05f;

        /// <summary>
        /// Marks the no-chord pick in the decoded path.
        /// </summary>
        internal const int NoChordState = -1;

        private readonly float _noChordEmission;

        /// <summary>
        /// noChordEmission is what a candidate has to beat to be worth picking — pass the
        /// caller's confidence threshold and it behaves like the old per-window cutoff.
        /// </summary>
        internal ChordProgressionDecoder(float noChordEmission)
        {
            _noChordEmission = noChordEmission;
        }

        /// <summary>
        /// Viterbi over the lattice.
        /// </summary>
        /// <returns>Per window: the chosen candidate index, or NoChordState.</returns>
        internal int[] Decode(IReadOnlyList<ChordWindow> windows)
        {
            int windowCount = windows.Count;
            var selection = new int[windowCount];

            if (windowCount == 0) return selection;

            var scores = new float[windowCount][];
            var backpointers = new int[windowCount][];

            int firstStates = _stateCount(windows[0]);
            scores[0] = new float[firstStates];
            backpointers[0] = new int[firstStates];

            for (int s = 0; s < firstStates; s++)
            {
                scores[0][s] = _emission(windows[0], s);
                backpointers[0][s] = 0;
            }

            for (int t = 1; t < windowCount; t++)
            {
                int stateCount = _stateCount(windows[t]);
                int previousCount = scores[t - 1].Length;

                scores[t] = new float[stateCount];
                backpointers[t] = new int[stateCount];

                for (int s = 0; s < stateCount; s++)
                {
                    float best = float.MinValue;
                    int bestPrevious = 0;

                    for (int p = 0; p < previousCount; p++)
                    {
                        float candidateScore = scores[t - 1][p]
                            - _transitionPenalty(windows[t - 1], p, windows[t], s);

                        if (candidateScore > best)
                        {
                            best = candidateScore;
                            bestPrevious = p;
                        }
                    }

                    scores[t][s] = best + _emission(windows[t], s);
                    backpointers[t][s] = bestPrevious;
                }
            }

            int lastCount = scores[windowCount - 1].Length;
            int state = 0;
            float bestFinal = float.MinValue;

            for (int s = 0; s < lastCount; s++)
            {
                if (scores[windowCount - 1][s] > bestFinal)
                {
                    bestFinal = scores[windowCount - 1][s];
                    state = s;
                }
            }

            for (int t = windowCount - 1; t >= 0; t--)
            {
                selection[t] = state < windows[t].Candidates.Length ? state : NoChordState;
                state = backpointers[t][state];
            }

            return selection;
        }

        private static int _stateCount(in ChordWindow window)
        {
            return window.Candidates.Length + 1;
        }

        private float _emission(in ChordWindow window, int state)
        {
            return state < window.Candidates.Length
                ? window.Candidates[state].Score
                : _noChordEmission;
        }

        /// <summary>
        /// Same chord (or no-chord to no-chord) is free, chord to chord costs the base penalty
        /// plus fifths distance, crossing the no-chord boundary costs less.
        /// </summary>
        private static float _transitionPenalty(
            in ChordWindow previous, int previousState,
            in ChordWindow current, int currentState)
        {
            bool previousIsChord = previousState < previous.Candidates.Length;
            bool currentIsChord = currentState < current.Candidates.Length;

            if (!previousIsChord && !currentIsChord) return 0f;
            if (previousIsChord != currentIsChord) return NoChordTransitionPenalty;

            ref readonly ChordCandidate from = ref previous.Candidates[previousState];
            ref readonly ChordCandidate to = ref current.Candidates[currentState];

            if (string.Equals(from.Name, to.Name, StringComparison.Ordinal)) return 0f;

            return ChordChangePenalty + FifthsDistanceWeight * _fifthsDistance(from.RootPitchClass, to.RootPitchClass);
        }

        /// <summary>
        /// 0-6 steps around the circle of fifths. C-G or C-F is 1, tritone is 6.
        /// </summary>
        private static int _fifthsDistance(int pitchClassA, int pitchClassB)
        {
            int positionA = pitchClassA * 7 % 12;
            int positionB = pitchClassB * 7 % 12;
            int difference = Math.Abs(positionA - positionB);
            return Math.Min(difference, 12 - difference);
        }
    }
}
