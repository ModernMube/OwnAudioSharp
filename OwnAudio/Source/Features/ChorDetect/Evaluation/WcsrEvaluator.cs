using System;
using System.Collections.Generic;
using OwnaudioNET.Features.OwnChordDetect.Analysis;

namespace OwnaudioNET.Features.OwnChordDetect.Evaluation
{
    /// <summary>
    /// What a WCSR run gives back: seconds matched, seconds scored, and the ratio.
    /// </summary>
    internal readonly struct WcsrResult
    {
        /// <summary>
        /// Seconds where we agreed with the reference.
        /// </summary>
        internal readonly float MatchedDuration;

        /// <summary>
        /// Seconds actually scored — out-of-vocabulary reference bits don't count.
        /// </summary>
        internal readonly float ScoredDuration;

        /// <summary>
        /// Both totals in seconds.
        /// </summary>
        internal WcsrResult(float matchedDuration, float scoredDuration)
        {
            MatchedDuration = matchedDuration;
            ScoredDuration = scoredDuration;
        }

        /// <summary>
        /// Matched over scored, 0 if nothing was scored.
        /// </summary>
        internal float Score => ScoredDuration > 0f ? MatchedDuration / ScoredDuration : 0f;
    }

    /// <summary>
    /// Weighted chord symbol recall against a ground truth — the MIREX chord estimation metric
    /// the way mir_eval does it: how much of the annotated time we got right once both sides
    /// are squashed to a vocabulary level.
    /// <para>
    /// Reference segments outside the vocabulary (sus/dim/aug at MajMin, or "X") drop out of the
    /// scored time. Gaps in our estimate count as no-chord, so they match "N" and miss the rest.
    /// One thing we do differently from mir_eval: sixths reduce to their parent triad instead of
    /// being excluded, since our vocabulary reports them.
    /// </para>
    /// </summary>
    internal static class WcsrEvaluator
    {
        /// <summary>
        /// An estimate segment ready for scoring: half-open interval plus the already parsed symbol.
        /// </summary>
        private readonly struct EstimateSegment
        {
            internal readonly float StartTime;
            internal readonly float EndTime;
            internal readonly ChordSymbol Symbol;

            internal EstimateSegment(float startTime, float endTime, ChordSymbol symbol)
            {
                StartTime = startTime;
                EndTime = endTime;
                Symbol = symbol;
            }
        }

        /// <summary>
        /// Scores estimates against the reference at the given vocabulary level. Overlapping
        /// estimate segments get clipped at the next start.
        /// </summary>
        internal static WcsrResult Evaluate(
            IReadOnlyList<ChordAnnotation> reference,
            IReadOnlyList<TimedChord> estimates,
            ChordComparisonLevel level)
        {
            if (reference == null || reference.Count == 0)
                return new WcsrResult(0f, 0f);

            var prepared = _prepareEstimates(estimates);
            var boundaries = _collectBoundaries(reference, prepared);

            float matched = 0f;
            float scored = 0f;

            for (int i = 0; i + 1 < boundaries.Count; i++)
            {
                float start = boundaries[i];
                float end = boundaries[i + 1];
                if (end <= start) continue;

                float midpoint = (start + end) * 0.5f;

                var referenceSymbol = _findReferenceSymbol(reference, midpoint);
                if (referenceSymbol == null) continue;

                var referenceClass = referenceSymbol.Value.Reduce(level);
                if (referenceClass == ComparisonQuality.Excluded) continue;

                float duration = end - start;
                scored += duration;

                var estimateSymbol = _findEstimateSymbol(prepared, midpoint);

                if (estimateSymbol.Reduce(level) == referenceClass
                    && (referenceClass == ComparisonQuality.NoChord
                        || referenceSymbol.Value.RootPitchClass == estimateSymbol.RootPitchClass))
                {
                    matched += duration;
                }
            }

            return new WcsrResult(matched, scored);
        }

        /// <summary>
        /// Sorts, clips overlaps at the next start (later chord wins, that's the change point)
        /// and parses every name once.
        /// </summary>
        private static List<EstimateSegment> _prepareEstimates(IReadOnlyList<TimedChord> estimates)
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
                {
                    prepared.Add(new EstimateSegment(
                        sorted[i].StartTime, end, ChordSymbol.ParseDetectorName(sorted[i].ChordName)));
                }
            }

            return prepared;
        }

        private static List<float> _collectBoundaries(
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

        private static ChordSymbol? _findReferenceSymbol(IReadOnlyList<ChordAnnotation> reference, float time)
        {
            foreach (var segment in reference)
            {
                if (time >= segment.StartTime && time < segment.EndTime) return segment.Symbol;
            }

            return null;
        }

        /// <summary>
        /// Gaps count as no-chord.
        /// </summary>
        private static ChordSymbol _findEstimateSymbol(List<EstimateSegment> estimates, float time)
        {
            foreach (var segment in estimates)
            {
                if (time >= segment.StartTime && time < segment.EndTime) return segment.Symbol;
            }

            return new ChordSymbol(-1, ChordQuality.NoChord);
        }
    }
}
