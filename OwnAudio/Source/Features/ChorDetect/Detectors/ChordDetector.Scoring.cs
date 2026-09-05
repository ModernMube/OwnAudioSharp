using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace OwnaudioNET.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// Chromagram building and the ranking pass that turns it into a chord name.
    /// </summary>
    public partial class ChordDetector
    {
        /// <summary>
        /// The real one. Ranking happens on the perceptual score, but the confidence we report is
        /// always the raw cosine, so the threshold keeps meaning what it always meant. Ambiguity
        /// compares scores too — a bare triad and its unsupported seventh aren't "ambiguous" anymore,
        /// the priors already separated them. bassPitchClass of -1 means we don't know the bass.
        /// </summary>
        internal (string chord, float confidence, bool isAmbiguous, string[] alternatives) DetectChordAdvanced(float[]? chromagram, int bassPitchClass)
        {
            if (chromagram == null || _templateEntries.Length == 0)
                return ("Unknown", 0f, false, new string[0]);

            Span<ScoredChord> top = stackalloc ScoredChord[TopCandidateCount];
            int count = _rankChords(chromagram, bassPitchClass, top);

            if (count == 0) return ("Unknown", 0f, false, new string[0]);

            ScoredChord best = top[0];

            if (_mode == DetectionMode.Optimized)
            {
                int ambiguousCount = 0;
                for (int i = 0; i < count; i++)
                {
                    if (best.Score - top[i].Score <= _ambiguityThreshold) ambiguousCount++;
                }

                bool isAmbiguous = ambiguousCount > 1;

                if (best.Cosine >= _confidenceThreshold)
                {
                    if (isAmbiguous)
                    {
                        var ambiguous = new string[ambiguousCount];
                        for (int i = 0, w = 0; i < count && w < ambiguousCount; i++)
                        {
                            if (best.Score - top[i].Score <= _ambiguityThreshold)
                                ambiguous[w++] = _templateEntries[top[i].TemplateIndex].Name;
                        }

                        var combinedName = string.Join("/", ambiguous, 0, Math.Min(3, ambiguous.Length));
                        return (combinedName, best.Cosine, true, ambiguous);
                    }

                    return (_templateEntries[best.TemplateIndex].Name, best.Cosine, false, new string[0]);
                }

                int altCount = Math.Min(3, count);
                var alternatives = new string[altCount];
                for (int i = 0; i < altCount; i++)
                    alternatives[i] = _templateEntries[top[i].TemplateIndex].Name;

                return ("Unknown", best.Cosine, false, alternatives);
            }

            var chord = best.Cosine >= _confidenceThreshold
                ? _templateEntries[best.TemplateIndex].Name
                : "Unknown";
            return (chord, best.Cosine, false, new string[0]);
        }

        /// <summary>
        /// Pitch class histogram weighted by amplitude times sounding time, then normalized —
        /// a held note counts for more than an ornament. Pass -1 for the bounds to use full
        /// note durations instead of the window overlap.
        /// </summary>
        public float[] ComputeChromagram(List<Note> notes, float windowStart = -1f, float windowEnd = -1f)
        {
            var chroma = new float[12];

            foreach (var note in notes)
                chroma[note.Pitch % 12] += note.Amplitude * _effectiveDuration(note, windowStart, windowEnd);

            float sum = 0f;
            for (int i = 0; i < 12; i++)
                sum += chroma[i];

            if (sum > 0f)
            {
                float inverseSum = 1f / sum;
                for (int i = 0; i < 12; i++)
                    chroma[i] *= inverseSum;
            }

            return chroma;
        }

        /// <summary>
        /// The hot loop. Scores every template and keeps the best ones in the caller's buffer
        /// (usually stack allocated) — no allocations, no LINQ, insertion sort on the way in.
        /// <para>
        /// Score is cosine minus the parsimony penalty, minus the missing-tone penalty, plus the
        /// diatonic and bass-root bonuses. The raw cosine travels along untouched so callers can
        /// report honest confidence.
        /// </para>
        /// </summary>
        /// <returns>How many candidates actually landed in the buffer.</returns>
        private int _rankChords(float[] chromagram, int bassPitchClass, Span<ScoredChord> top)
        {
            float chromaMagnitudeSquared = 0f;
            float chromaMax = 0f;
            for (int i = 0; i < 12; i++)
            {
                float value = chromagram[i];
                chromaMagnitudeSquared += value * value;
                if (value > chromaMax) chromaMax = value;
            }

            if (chromaMagnitudeSquared <= 0f) return 0;

            float inverseChromaMagnitude = (float)(1.0 / Math.Sqrt(chromaMagnitudeSquared));
            float missingThreshold = chromaMax * MissingToneThresholdRatio;

            int capacity = top.Length;
            int filled = 0;
            var entries = _templateEntries;

            for (int e = 0; e < entries.Length; e++)
            {
                ref readonly TemplateEntry entry = ref entries[e];
                float[] vector = entry.Vector;

                float dot = 0f;
                float missingWeight = 0f;
                for (int pc = 0; pc < 12; pc++)
                {
                    float templateWeight = vector[pc];
                    float chromaValue = chromagram[pc];

                    dot += chromaValue * templateWeight;

                    if (templateWeight > 0f && chromaValue < missingThreshold)
                        missingWeight += templateWeight;
                }

                float cosine = dot * inverseChromaMagnitude * entry.InverseMagnitude;
                if (cosine <= 0f) continue;

                float score = cosine
                    - entry.ComplexityPenalty
                    - MissingTonePenaltyWeight * missingWeight * entry.InverseWeightSum;
                if (entry.IsDiatonic) score += DiatonicBonus;
                if (bassPitchClass >= 0 && entry.Root == bassPitchClass) score += BassRootBonus;

                if (filled == capacity && score <= top[capacity - 1].Score) continue;

                int j = filled < capacity ? filled : capacity - 1;
                while (j > 0 && top[j - 1].Score < score)
                {
                    top[j] = top[j - 1];
                    j--;
                }

                top[j] = new ScoredChord(e, cosine, score);

                if (filled < capacity) filled++;
            }

            return filled;
        }

        /// <summary>
        /// The sentence that ends up in ChordAnalysis.Explanation. noteNames comes from the
        /// present-notes pass, no point recomputing it.
        /// </summary>
        private string _explain(string[] noteNames, string chord, float confidence, bool isAmbiguous)
        {
            var keyInfo = _currentKey != null ? $" (Key: {_currentKey})" : "";

            if (noteNames.Length < 2)
                return $"Too few notes ({noteNames.Length}) for reliable chord detection{keyInfo}.";

            if (isAmbiguous)
                return $"Ambiguous chord with notes [{string.Join(", ", noteNames)}]{keyInfo}. Multiple interpretations possible.";

            return confidence switch
            {
                >= 0.9f => $"Clear {chord} chord with notes [{string.Join(", ", noteNames)}]{keyInfo}.",
                >= 0.7f => $"Likely {chord} chord with notes [{string.Join(", ", noteNames)}]{keyInfo}.",
                >= 0.5f => $"Possible {chord} chord with notes [{string.Join(", ", noteNames)}]{keyInfo}, but uncertain.",
                _ => $"Unclear chord with notes [{string.Join(", ", noteNames)}]{keyInfo}. Consider adding more notes."
            };
        }
    }
}
