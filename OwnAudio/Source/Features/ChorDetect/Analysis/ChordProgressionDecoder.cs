using System;
using System.Collections.Generic;
using OwnaudioNET.Features.OwnChordDetect.Detectors;

namespace OwnaudioNET.Features.OwnChordDetect.Analysis
{
    /// <summary>
    /// One analysis window of the chord lattice: its time span and the ranked chord
    /// hypotheses detected in it. An empty candidate array marks a window that can only
    /// be labelled as no-chord (too few notes or no usable energy).
    /// </summary>
    internal readonly struct ChordWindow
    {
        /// <summary>
        /// The start time of the window in seconds.
        /// </summary>
        internal readonly float StartTime;

        /// <summary>
        /// The end time of the window in seconds.
        /// </summary>
        internal readonly float EndTime;

        /// <summary>
        /// The ranked chord candidates of the window in descending score order.
        /// </summary>
        internal readonly ChordCandidate[] Candidates;

        /// <summary>
        /// Initializes a new chord window.
        /// </summary>
        /// <param name="startTime">The start time of the window in seconds.</param>
        /// <param name="endTime">The end time of the window in seconds.</param>
        /// <param name="candidates">The ranked chord candidates of the window.</param>
        internal ChordWindow(float startTime, float endTime, ChordCandidate[] candidates)
        {
            StartTime = startTime;
            EndTime = endTime;
            Candidates = candidates;
        }
    }

    /// <summary>
    /// Decodes the most plausible chord progression over a sequence of analysis windows
    /// using Viterbi dynamic programming, the standard temporal-smoothing approach of
    /// automatic chord estimation systems (Sheh &amp; Ellis 2003, Bello &amp; Pickens 2005).
    /// <para>
    /// Instead of letting every window vote independently, the decoder maximises a
    /// sequence score in which chord changes carry a musically informed cost: staying on
    /// the same chord is free, switching is penalised proportionally to the circle-of-fifths
    /// distance between the roots, and an explicit no-chord state absorbs windows where
    /// nothing matches convincingly. This removes frame-level flicker and prefers
    /// harmonically coherent progressions without overriding sustained evidence
    /// of a genuine chord change.
    /// </para>
    /// </summary>
    internal sealed class ChordProgressionDecoder
    {
        /// <summary>
        /// Base score penalty for switching between two different chords in consecutive
        /// windows. A real chord change sustains its score advantage over many windows,
        /// so it still wins; single-window detection flicker does not. Sized above the
        /// score gain an extension relabel (e.g. F to Fadd9) can earn on a chord-transition
        /// mixture window, so root-preserving label flicker is suppressed too.
        /// </summary>
        private const float ChordChangePenalty = 0.10f;

        /// <summary>
        /// Additional per-step penalty weight multiplied by the circle-of-fifths distance
        /// (0-6) between the roots of the outgoing and incoming chords. Encodes the musical
        /// prior that progressions move mostly by closely related harmonies.
        /// </summary>
        private const float FifthsDistanceWeight = 0.008f;

        /// <summary>
        /// Penalty for entering or leaving the no-chord state. Kept below
        /// <see cref="ChordChangePenalty"/> so silence and noise boundaries are cheaper
        /// to cross than an implausible chord-to-chord jump.
        /// </summary>
        private const float NoChordTransitionPenalty = 0.05f;

        /// <summary>
        /// The state index used to mark the no-chord state in the decoded path.
        /// </summary>
        internal const int NoChordState = -1;

        /// <summary>
        /// The emission score of the no-chord state. A chord candidate must score above
        /// this to be worth selecting, so it plays the role of the former per-window
        /// confidence threshold inside the sequence model.
        /// </summary>
        private readonly float _noChordEmission;

        /// <summary>
        /// Initializes a new progression decoder.
        /// </summary>
        /// <param name="noChordEmission">
        /// The emission score of the no-chord state, typically the caller's confidence
        /// threshold so windows below it fall to no-chord exactly as they previously
        /// fell below the acceptance threshold.
        /// </param>
        internal ChordProgressionDecoder(float noChordEmission)
        {
            _noChordEmission = noChordEmission;
        }

        /// <summary>
        /// Runs Viterbi decoding over the window lattice and returns, for every window,
        /// the index of the selected candidate or <see cref="NoChordState"/> when the
        /// no-chord state was chosen.
        /// </summary>
        /// <param name="windows">The chord lattice built from the analysis windows.</param>
        /// <returns>An array of per-window selections aligned with <paramref name="windows"/>.</returns>
        internal int[] Decode(IReadOnlyList<ChordWindow> windows)
        {
            int windowCount = windows.Count;
            var selection = new int[windowCount];

            if (windowCount == 0)
                return selection;

            var scores = new float[windowCount][];
            var backpointers = new int[windowCount][];

            int firstStates = StateCount(windows[0]);
            scores[0] = new float[firstStates];
            backpointers[0] = new int[firstStates];

            for (int s = 0; s < firstStates; s++)
            {
                scores[0][s] = Emission(windows[0], s);
                backpointers[0][s] = 0;
            }

            for (int t = 1; t < windowCount; t++)
            {
                int stateCount = StateCount(windows[t]);
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
                            - TransitionPenalty(windows[t - 1], p, windows[t], s);

                        if (candidateScore > best)
                        {
                            best = candidateScore;
                            bestPrevious = p;
                        }
                    }

                    scores[t][s] = best + Emission(windows[t], s);
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

        /// <summary>
        /// Returns the number of Viterbi states of a window: its candidates plus the
        /// always-present no-chord state as the last index.
        /// </summary>
        /// <param name="window">The window whose states are counted.</param>
        /// <returns>The state count including the no-chord state.</returns>
        private static int StateCount(in ChordWindow window)
        {
            return window.Candidates.Length + 1;
        }

        /// <summary>
        /// Returns the emission score of a state: the candidate's perceptual score, or the
        /// configured no-chord emission for the no-chord state.
        /// </summary>
        /// <param name="window">The window the state belongs to.</param>
        /// <param name="state">The state index within the window.</param>
        /// <returns>The emission score of the state.</returns>
        private float Emission(in ChordWindow window, int state)
        {
            return state < window.Candidates.Length
                ? window.Candidates[state].Score
                : _noChordEmission;
        }

        /// <summary>
        /// Computes the musically weighted transition penalty between a state of the
        /// previous window and a state of the current window. Staying on the same chord
        /// name (or in no-chord) is free; chord-to-chord moves cost the base change penalty
        /// plus the circle-of-fifths distance between the roots; moves into or out of
        /// no-chord cost a smaller boundary penalty.
        /// </summary>
        /// <param name="previous">The previous window.</param>
        /// <param name="previousState">The state index within the previous window.</param>
        /// <param name="current">The current window.</param>
        /// <param name="currentState">The state index within the current window.</param>
        /// <returns>The transition penalty to subtract from the accumulated score.</returns>
        private static float TransitionPenalty(
            in ChordWindow previous, int previousState,
            in ChordWindow current, int currentState)
        {
            bool previousIsChord = previousState < previous.Candidates.Length;
            bool currentIsChord = currentState < current.Candidates.Length;

            if (!previousIsChord && !currentIsChord)
                return 0f;

            if (previousIsChord != currentIsChord)
                return NoChordTransitionPenalty;

            ref readonly ChordCandidate from = ref previous.Candidates[previousState];
            ref readonly ChordCandidate to = ref current.Candidates[currentState];

            if (string.Equals(from.Name, to.Name, StringComparison.Ordinal))
                return 0f;

            int distance = CircleOfFifthsDistance(from.RootPitchClass, to.RootPitchClass);
            return ChordChangePenalty + FifthsDistanceWeight * distance;
        }

        /// <summary>
        /// Computes the distance between two pitch classes on the circle of fifths (0-6).
        /// Closely related harmonies (C-G, C-F) score 1; the tritone-related maximum is 6.
        /// </summary>
        /// <param name="pitchClassA">The first pitch class (0-11).</param>
        /// <param name="pitchClassB">The second pitch class (0-11).</param>
        /// <returns>The circle-of-fifths distance in the range 0-6.</returns>
        private static int CircleOfFifthsDistance(int pitchClassA, int pitchClassB)
        {
            int positionA = pitchClassA * 7 % 12;
            int positionB = pitchClassB * 7 % 12;
            int difference = Math.Abs(positionA - positionB);
            return Math.Min(difference, 12 - difference);
        }
    }
}
