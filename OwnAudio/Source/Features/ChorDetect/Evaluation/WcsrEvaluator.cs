using System;
using System.Collections.Generic;
using OwnaudioNET.Features.OwnChordDetect.Analysis;

namespace OwnaudioNET.Features.OwnChordDetect.Evaluation
{
    /// <summary>
    /// Result of a WCSR evaluation: the matched and scored durations and their ratio.
    /// </summary>
    internal readonly struct WcsrResult
    {
        /// <summary>
        /// Total duration (seconds) where the estimate matched the reference.
        /// </summary>
        internal readonly float MatchedDuration;

        /// <summary>
        /// Total scored reference duration (seconds), excluding out-of-vocabulary segments.
        /// </summary>
        internal readonly float ScoredDuration;

        /// <summary>
        /// Initializes a new WCSR result.
        /// </summary>
        /// <param name="matchedDuration">Total matched duration in seconds.</param>
        /// <param name="scoredDuration">Total scored reference duration in seconds.</param>
        internal WcsrResult(float matchedDuration, float scoredDuration)
        {
            MatchedDuration = matchedDuration;
            ScoredDuration = scoredDuration;
        }

        /// <summary>
        /// The weighted chord symbol recall in [0,1]: matched duration over scored duration,
        /// or 0 when nothing was scored.
        /// </summary>
        internal float Score => ScoredDuration > 0f ? MatchedDuration / ScoredDuration : 0f;
    }

    /// <summary>
    /// Computes the weighted chord symbol recall (WCSR) of a chord estimate against a
    /// ground-truth annotation — the standard metric of the MIREX Audio Chord Estimation
    /// task as implemented by mir_eval: the fraction of annotated time where the estimated
    /// chord matches the reference after both are reduced to a vocabulary level.
    /// <para>
    /// Reference segments outside the vocabulary (e.g. sus/dim/aug at the MajMin level,
    /// or "X" regions) are excluded from the scored duration. Gaps in the estimate count
    /// as no-chord, so they match reference "N" segments and miss everything else.
    /// One documented deviation from mir_eval: sixth chords reduce to their parent
    /// triad instead of being excluded, because the OwnAudio vocabulary reports them.
    /// </para>
    /// </summary>
    internal static class WcsrEvaluator
    {
        /// <summary>
        /// An estimate segment prepared for evaluation: a half-open time interval with the
        /// pre-reduced comparison symbol of the detected chord.
        /// </summary>
        private readonly struct EstimateSegment
        {
            /// <summary>
            /// The start time of the segment in seconds.
            /// </summary>
            internal readonly float StartTime;

            /// <summary>
            /// The effective end time of the segment in seconds (clipped at the next segment's start).
            /// </summary>
            internal readonly float EndTime;

            /// <summary>
            /// The parsed chord symbol of the segment.
            /// </summary>
            internal readonly ChordSymbol Symbol;

            /// <summary>
            /// Initializes a new prepared estimate segment.
            /// </summary>
            /// <param name="startTime">The start time in seconds.</param>
            /// <param name="endTime">The effective end time in seconds.</param>
            /// <param name="symbol">The parsed chord symbol.</param>
            internal EstimateSegment(float startTime, float endTime, ChordSymbol symbol)
            {
                StartTime = startTime;
                EndTime = endTime;
                Symbol = symbol;
            }
        }

        /// <summary>
        /// Evaluates a chord estimate against a reference annotation at the given vocabulary level.
        /// </summary>
        /// <param name="reference">The ground-truth segments (non-overlapping, chronological).</param>
        /// <param name="estimates">The detected timed chords; overlapping segments are clipped at the next start.</param>
        /// <param name="level">The comparison vocabulary level.</param>
        /// <returns>The WCSR result with matched and scored durations.</returns>
        internal static WcsrResult Evaluate(
            IReadOnlyList<ChordAnnotation> reference,
            IReadOnlyList<TimedChord> estimates,
            ChordComparisonLevel level)
        {
            if (reference == null || reference.Count == 0)
                return new WcsrResult(0f, 0f);

            var prepared = PrepareEstimates(estimates);
            var boundaries = CollectBoundaries(reference, prepared);

            float matched = 0f;
            float scored = 0f;

            for (int i = 0; i + 1 < boundaries.Count; i++)
            {
                float start = boundaries[i];
                float end = boundaries[i + 1];
                if (end <= start)
                    continue;

                float midpoint = (start + end) * 0.5f;

                var referenceSymbol = FindReferenceSymbol(reference, midpoint);
                if (referenceSymbol == null)
                    continue;

                var referenceClass = referenceSymbol.Value.Reduce(level);
                if (referenceClass == ComparisonQuality.Excluded)
                    continue;

                float duration = end - start;
                scored += duration;

                var estimateSymbol = FindEstimateSymbol(prepared, midpoint);
                var estimateClass = estimateSymbol.Reduce(level);

                if (estimateClass == referenceClass && RootsMatch(referenceSymbol.Value, estimateSymbol, referenceClass))
                    matched += duration;
            }

            return new WcsrResult(matched, scored);
        }

        /// <summary>
        /// Compares the roots of two symbols; the no-chord class matches without roots.
        /// </summary>
        /// <param name="reference">The reference symbol.</param>
        /// <param name="estimate">The estimate symbol.</param>
        /// <param name="comparisonClass">The already-matching comparison class.</param>
        /// <returns>True when the roots agree (or the class is no-chord).</returns>
        private static bool RootsMatch(ChordSymbol reference, ChordSymbol estimate, ComparisonQuality comparisonClass)
        {
            return comparisonClass == ComparisonQuality.NoChord
                || reference.RootPitchClass == estimate.RootPitchClass;
        }

        /// <summary>
        /// Sorts the estimates, clips overlapping segments at the next segment's start
        /// (later chords win the overlap, marking the change point) and parses each name once.
        /// </summary>
        /// <param name="estimates">The detected timed chords.</param>
        /// <returns>The prepared, non-overlapping estimate segments.</returns>
        private static List<EstimateSegment> PrepareEstimates(IReadOnlyList<TimedChord> estimates)
        {
            var sorted = new List<TimedChord>(estimates ?? Array.Empty<TimedChord>());
            sorted.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

            var prepared = new List<EstimateSegment>(sorted.Count);
            for (int i = 0; i < sorted.Count; i++)
            {
                float end = sorted[i].EndTime;
                if (i + 1 < sorted.Count && sorted[i + 1].StartTime < end)
                    end = sorted[i + 1].StartTime;

                if (end > sorted[i].StartTime)
                    prepared.Add(new EstimateSegment(
                        sorted[i].StartTime, end, ChordSymbol.ParseDetectorName(sorted[i].ChordName)));
            }

            return prepared;
        }

        /// <summary>
        /// Collects the sorted union of all reference and estimate boundary times.
        /// </summary>
        /// <param name="reference">The reference segments.</param>
        /// <param name="estimates">The prepared estimate segments.</param>
        /// <returns>The sorted list of elementary interval boundaries.</returns>
        private static List<float> CollectBoundaries(
            IReadOnlyList<ChordAnnotation> reference,
            List<EstimateSegment> estimates)
        {
            var boundaries = new List<float>(reference.Count * 2 + estimates.Count * 2);

            foreach (var segment in reference)
            {
                boundaries.Add(segment.StartTime);
                boundaries.Add(segment.EndTime);
            }

            foreach (var segment in estimates)
            {
                boundaries.Add(segment.StartTime);
                boundaries.Add(segment.EndTime);
            }

            boundaries.Sort();
            return boundaries;
        }

        /// <summary>
        /// Finds the reference symbol covering a time point, or null when unannotated.
        /// </summary>
        /// <param name="reference">The reference segments.</param>
        /// <param name="time">The time point in seconds.</param>
        /// <returns>The covering symbol, or null.</returns>
        private static ChordSymbol? FindReferenceSymbol(IReadOnlyList<ChordAnnotation> reference, float time)
        {
            foreach (var segment in reference)
            {
                if (time >= segment.StartTime && time < segment.EndTime)
                    return segment.Symbol;
            }

            return null;
        }

        /// <summary>
        /// Finds the estimate symbol covering a time point; gaps count as no-chord.
        /// </summary>
        /// <param name="estimates">The prepared estimate segments.</param>
        /// <param name="time">The time point in seconds.</param>
        /// <returns>The covering symbol, or a no-chord symbol for gaps.</returns>
        private static ChordSymbol FindEstimateSymbol(List<EstimateSegment> estimates, float time)
        {
            foreach (var segment in estimates)
            {
                if (time >= segment.StartTime && time < segment.EndTime)
                    return segment.Symbol;
            }

            return new ChordSymbol(-1, ChordQuality.NoChord);
        }
    }
}
