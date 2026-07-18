using System;
using System.Collections.Generic;
using OwnaudioNET.Features.Extensions;

namespace OwnaudioNET.Features.OwnChordDetect.Core
{
    /// <summary>
    /// A key with its signature and how it likes its notes spelled.
    /// </summary>
    public class MusicalKey
    {
        /// <summary>
        /// "C", "F#", "Bbm" and friends.
        /// </summary>
        public string KeyName { get; }

        /// <summary>
        /// True for major, false for minor.
        /// </summary>
        public bool IsMajor { get; }

        /// <summary>
        /// Sharps in the signature, 0-7.
        /// </summary>
        public int Sharps { get; }

        /// <summary>
        /// Flats in the signature, 0-7.
        /// </summary>
        public int Flats { get; }

        /// <summary>
        /// The 12 note names to use here — decides sharps vs flats everywhere downstream.
        /// </summary>
        public string[] PreferredNoteNames { get; }

        /// <summary>
        /// Everything gets passed in, nothing is derived here.
        /// </summary>
        public MusicalKey(string keyName, bool isMajor, int sharps, int flats, string[] preferredNoteNames)
        {
            KeyName = keyName;
            IsMajor = isMajor;
            Sharps = sharps;
            Flats = flats;
            PreferredNoteNames = preferredNoteNames;
        }

        /// <summary>
        /// "C major" / "A minor".
        /// </summary>
        public override string ToString() => $"{KeyName} {(IsMajor ? "major" : "minor")}";
    }

    /// <summary>
    /// A key holding for one stretch of the song. No modulation means one single segment.
    /// </summary>
    internal sealed class TimedKey
    {
        /// <summary>
        /// Segment start, seconds.
        /// </summary>
        internal float StartTime { get; }

        /// <summary>
        /// Segment end, seconds.
        /// </summary>
        internal float EndTime { get; }

        /// <summary>
        /// What's in force here.
        /// </summary>
        internal MusicalKey Key { get; }

        /// <summary>
        /// Span plus key.
        /// </summary>
        internal TimedKey(float startTime, float endTime, MusicalKey key)
        {
            StartTime = startTime;
            EndTime = endTime;
            Key = key;
        }

        /// <summary>
        /// Span and key, for the log.
        /// </summary>
        public override string ToString() => $"{StartTime:F1}s-{EndTime:F1}s: {Key}";
    }

    /// <summary>
    /// Key detection the Krumhansl-Schmuckler way: correlate the pitch class histogram
    /// against the 30 key profiles and take the winner.
    /// </summary>
    public class KeyDetector
    {
        private static readonly float[] _majorProfile =
        {
            6.35f, 2.23f, 3.48f, 2.33f, 4.38f, 4.09f, 2.52f, 5.19f, 2.39f, 3.66f, 2.29f, 2.88f
        };

        private static readonly float[] _minorProfile =
        {
            6.33f, 2.68f, 3.52f, 5.38f, 2.60f, 3.53f, 2.54f, 4.75f, 3.98f, 2.69f, 3.34f, 3.17f
        };

        /// <summary>
        /// Every key we know, with its spelling preference and tonic pitch class.
        /// </summary>
        private static readonly (string name, bool isMajor, bool useFlats, int tonic)[] _keyDefs =
        {
            ("C", true, false, 0), ("G", true, false, 7), ("D", true, false, 2), ("A", true, false, 9),
            ("E", true, false, 4), ("B", true, false, 11), ("F#", true, false, 6), ("C#", true, false, 1),

            ("F", true, true, 5), ("Bb", true, true, 10), ("Eb", true, true, 3), ("Ab", true, true, 8),
            ("Db", true, true, 1), ("Gb", true, true, 6), ("Cb", true, true, 11),

            ("Am", false, false, 9), ("Em", false, false, 4), ("Bm", false, false, 11), ("F#m", false, false, 6),
            ("C#m", false, false, 1), ("G#m", false, false, 8), ("D#m", false, false, 3), ("A#m", false, false, 10),

            ("Dm", false, true, 2), ("Gm", false, true, 7), ("Cm", false, true, 0), ("Fm", false, true, 5),
            ("Bbm", false, true, 10), ("Ebm", false, true, 3), ("Abm", false, true, 8)
        };

        private static readonly string[] _sharpNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        private static readonly string[] _flatNames = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

        private static readonly string[] _sharpOrder = { "C", "G", "D", "A", "E", "B", "F#", "C#" };
        private static readonly string[] _minorSharpOrder = { "Am", "Em", "Bm", "F#m", "C#m", "G#m", "D#m", "A#m" };
        private static readonly string[] _flatOrder = { "C", "F", "Bb", "Eb", "Ab", "Db", "Gb", "Cb" };
        private static readonly string[] _minorFlatOrder = { "Am", "Dm", "Gm", "Cm", "Fm", "Bbm", "Ebm", "Abm" };

        /// <summary>
        /// Global key of the whole note list. Falls back to C major when there's nothing to look at.
        /// </summary>
        public MusicalKey DetectKey(List<Note> notes)
        {
            if (notes.Count == 0)
                return _createKey("C", true, false, 0);

            var chromagram = _chromagram(notes);
            var bestKey = _keyDefs[0];
            var bestCorrelation = float.MinValue;

            foreach (var keyDef in _keyDefs)
            {
                var correlation = _keyCorrelation(chromagram, keyDef);
                if (correlation > bestCorrelation)
                {
                    bestCorrelation = correlation;
                    bestKey = keyDef;
                }
            }

            return _createKey(bestKey.name, bestKey.isMajor, bestKey.useFlats, bestKey.tonic);
        }

        /// <summary>
        /// Keys need way more context than chords, hence the fat window.
        /// </summary>
        private const float DefaultKeyWindowSeconds = 8f;

        /// <summary>
        /// Step between key windows.
        /// </summary>
        private const float DefaultKeyHopSeconds = 2f;

        /// <summary>
        /// Cost of changing key. Big enough that one shaky window can't flip it, small enough
        /// that a real modulation gets through after a couple of windows.
        /// </summary>
        private const float KeyChangePenalty = 0.3f;

        /// <summary>
        /// Extra cost per step of fifths distance — modulations mostly go somewhere close.
        /// </summary>
        private const float TonicDistanceWeight = 0.01f;

        /// <summary>
        /// Key timeline with modulation tracking: overlapping windows, correlation against every
        /// profile, then Viterbi over the whole thing so changes have to earn their place.
        /// A song that never modulates comes back as one segment, same as DetectKey would say.
        /// </summary>
        /// <returns>Chronological segments, empty when there are no notes.</returns>
        internal List<TimedKey> DetectKeyTimeline(
            List<Note> notes,
            float windowSeconds = DefaultKeyWindowSeconds,
            float hopSeconds = DefaultKeyHopSeconds)
        {
            var timeline = new List<TimedKey>();

            if (notes == null || notes.Count == 0) return timeline;

            float duration = 0f;
            foreach (var note in notes)
            {
                if (note.EndTime > duration) duration = note.EndTime;
            }

            if (duration <= 0f) return timeline;

            if (duration <= windowSeconds)
            {
                timeline.Add(new TimedKey(0f, duration, DetectKey(notes)));
                return timeline;
            }

            float lastStart = Math.Max(0f, duration - windowSeconds);
            var windowStarts = new List<float>();
            for (float time = 0f; time < lastStart; time += hopSeconds)
                windowStarts.Add(time);
            windowStarts.Add(lastStart);

            int windowCount = windowStarts.Count;
            int stateCount = _keyDefs.Length;

            var emissions = new float[windowCount][];
            for (int w = 0; w < windowCount; w++)
            {
                float windowEnd = Math.Min(windowStarts[w] + windowSeconds, duration);
                var chromagram = _windowChromagram(notes, windowStarts[w], windowEnd);

                emissions[w] = new float[stateCount];
                for (int s = 0; s < stateCount; s++)
                    emissions[w][s] = _keyCorrelation(chromagram, _keyDefs[s]);
            }

            var scores = new float[stateCount];
            var previousScores = new float[stateCount];
            var backpointers = new int[windowCount][];

            for (int s = 0; s < stateCount; s++)
                previousScores[s] = emissions[0][s];

            backpointers[0] = new int[stateCount];

            for (int w = 1; w < windowCount; w++)
            {
                backpointers[w] = new int[stateCount];

                for (int s = 0; s < stateCount; s++)
                {
                    float best = float.MinValue;
                    int bestPrevious = 0;

                    for (int p = 0; p < stateCount; p++)
                    {
                        float penalty = p == s
                            ? 0f
                            : KeyChangePenalty + TonicDistanceWeight
                                * _fifthsDistance(_keyDefs[p].tonic, _keyDefs[s].tonic);

                        float candidate = previousScores[p] - penalty;
                        if (candidate > best)
                        {
                            best = candidate;
                            bestPrevious = p;
                        }
                    }

                    scores[s] = best + emissions[w][s];
                    backpointers[w][s] = bestPrevious;
                }

                (previousScores, scores) = (scores, previousScores);
            }

            int state = 0;
            float bestFinal = float.MinValue;
            for (int s = 0; s < stateCount; s++)
            {
                if (previousScores[s] > bestFinal)
                {
                    bestFinal = previousScores[s];
                    state = s;
                }
            }

            var selection = new int[windowCount];
            for (int w = windowCount - 1; w >= 0; w--)
            {
                selection[w] = state;
                state = backpointers[w][state];
            }

            int segmentStart = 0;
            float segmentStartTime = 0f;
            for (int w = 1; w <= windowCount; w++)
            {
                if (w == windowCount || selection[w] != selection[segmentStart])
                {
                    var definition = _keyDefs[selection[segmentStart]];
                    float end = w == windowCount
                        ? duration
                        : (windowStarts[w - 1] + windowStarts[w] + windowSeconds) * 0.5f;

                    timeline.Add(new TimedKey(
                        segmentStartTime, end,
                        _createKey(definition.name, definition.isMajor, definition.useFlats, definition.tonic)));

                    segmentStartTime = end;
                    segmentStart = w;
                }
            }

            return timeline;
        }

        /// <summary>
        /// Pitch class weights for the notes touching a window, each scaled by amplitude times
        /// how long it actually overlaps. Normalized at the end.
        /// </summary>
        private static float[] _windowChromagram(List<Note> notes, float windowStart, float windowEnd)
        {
            var chroma = new float[12];

            foreach (var note in notes)
            {
                float overlap = Math.Min(note.EndTime, windowEnd) - Math.Max(note.StartTime, windowStart);
                if (overlap <= 0f) continue;

                chroma[note.Pitch % 12] += note.Amplitude * overlap;
            }

            return _normalize(chroma);
        }

        private static int _fifthsDistance(int pitchClassA, int pitchClassB)
        {
            int positionA = pitchClassA * 7 % 12;
            int positionB = pitchClassB * 7 % 12;
            int difference = Math.Abs(positionA - positionB);
            return Math.Min(difference, 12 - difference);
        }

        private MusicalKey _createKey(string name, bool isMajor, bool useFlats, int tonic)
        {
            var noteNames = useFlats ? _flatNames : _sharpNames;
            var sharps = useFlats ? 0 : _signatureCount(name, isMajor ? _sharpOrder : _minorSharpOrder);
            var flats = useFlats ? _signatureCount(name, isMajor ? _flatOrder : _minorFlatOrder) : 0;

            return new MusicalKey(name, isMajor, sharps, flats, noteNames);
        }

        /// <summary>
        /// Position in the sharp/flat order is the accidental count, 0 if we don't find it.
        /// </summary>
        private static int _signatureCount(string keyName, string[] order)
        {
            var index = Array.IndexOf(order, keyName);
            return index >= 0 ? index : 0;
        }

        private float _keyCorrelation(float[] chromagram, (string name, bool isMajor, bool useFlats, int tonic) keyDef)
        {
            return _correlation(chromagram, keyDef.isMajor ? _majorProfile : _minorProfile, keyDef.tonic);
        }

        /// <summary>
        /// Pitch class histogram over the whole list, weighted by amplitude times note length.
        /// </summary>
        private float[] _chromagram(List<Note> notes)
        {
            var chroma = new float[12];

            foreach (var note in notes)
                chroma[note.Pitch % 12] += note.Amplitude * (note.EndTime - note.StartTime);

            return _normalize(chroma);
        }

        private static float[] _normalize(float[] chroma)
        {
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
        /// Pearson between the chromagram and a profile rotated onto the given tonic.
        /// The rotation is done by index so we don't build a copy per key per window.
        /// </summary>
        private float _correlation(float[] x, float[] profile, int steps)
        {
            int n = x.Length;
            float sumX = 0f, sumY = 0f, sumXY = 0f, sumX2 = 0f, sumY2 = 0f;

            for (int i = 0; i < n; i++)
            {
                float xi = x[i];
                float yi = profile[(i - steps + 12) % 12];
                sumX += xi;
                sumY += yi;
                sumXY += xi * yi;
                sumX2 += xi * xi;
                sumY2 += yi * yi;
            }

            double numerator = (double)n * sumXY - (double)sumX * sumY;
            double denominator = Math.Sqrt(((double)n * sumX2 - (double)sumX * sumX) * ((double)n * sumY2 - (double)sumY * sumY));

            return denominator > 0 ? (float)(numerator / denominator) : 0f;
        }
    }
}
