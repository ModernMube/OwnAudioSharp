using System;
using System.Collections.Generic;
using OwnaudioNET.Features.Extensions;

namespace OwnaudioNET.Features.OwnChordDetect.Analysis
{
    /// <summary>
    /// Places the beats of a known tempo onto the note onsets — dynamic-programming beat tracking
    /// after Ellis (2007). The tempo alone says how long a beat is, not where the beats fall; this
    /// finds the phase, and since every beat picks its own predecessor the grid can follow a tempo
    /// that drifts or breathes instead of sliding off the music over the length of a song.
    /// </summary>
    internal static class BeatTracker
    {
        /// <summary>
        /// Onset envelope resolution, seconds per frame.
        /// </summary>
        private const float FrameSeconds = 0.01f;

        /// <summary>
        /// Width of the Gaussian each onset is spread with, in frames. Transcribed onsets jitter
        /// by a few tens of milliseconds between the notes of one chord.
        /// </summary>
        private const float OnsetSpreadFrames = 2f;

        /// <summary>
        /// How hard a beat interval is pulled towards the nominal period. With the envelope at
        /// unit deviation a clear onset is worth a beat stretched by roughly ten percent.
        /// </summary>
        private const float Tightness = 100f;

        /// <summary>
        /// Shortest and longest beat the tracker may place, relative to the nominal period.
        /// Narrow enough that a half or a double beat can never sneak in.
        /// </summary>
        private const float MinimumIntervalRatio = 0.8f;
        private const float MaximumIntervalRatio = 1.25f;

        /// <summary>
        /// Beat times in seconds, ascending. The first one sits at or before zero and the last one
        /// at least a period past the end, so a window grid built on them covers the whole song.
        /// Too little material to track falls back to a plain grid from zero.
        /// </summary>
        internal static List<float> Track(List<Note> notes, float period, float duration)
        {
            var beats = new List<float>();
            if (period <= 0f || duration <= 0f) return beats;

            float periodFrames = period / FrameSeconds;
            int minimumLag = Math.Max(1, (int)Math.Floor(periodFrames * MinimumIntervalRatio));
            int maximumLag = Math.Max(minimumLag, (int)Math.Ceiling(periodFrames * MaximumIntervalRatio));
            int frameCount = (int)Math.Ceiling(duration / FrameSeconds) + 1;

            var envelope = frameCount > maximumLag ? _onsetEnvelope(notes, period, frameCount) : null;

            if (envelope != null)
            {
                foreach (int frame in _trackFrames(envelope, periodFrames, minimumLag, maximumLag))
                    beats.Add(frame * FrameSeconds);
            }

            if (beats.Count == 0) beats.Add(0f);

            while (beats[0] > 0f)
                beats.Insert(0, beats[0] - period);

            while (beats[beats.Count - 1] < duration + period)
                beats.Add(beats[beats.Count - 1] + period);

            return beats;
        }

        /// <summary>
        /// Onset strength per frame: every note adds amplitude times its length, capped at one
        /// beat, so held chord tones outweigh ornaments. Scaled to unit standard deviation, null
        /// when there is nothing to track.
        /// </summary>
        private static float[]? _onsetEnvelope(List<Note> notes, float period, int frameCount)
        {
            var impulses = new float[frameCount];

            foreach (var note in notes)
            {
                int frame = (int)Math.Round(note.StartTime / FrameSeconds);
                if (frame < 0 || frame >= frameCount) continue;

                float length = Math.Min(Math.Max(note.EndTime - note.StartTime, 0f), period);
                impulses[frame] += note.Amplitude * length;
            }

            int radius = (int)Math.Ceiling(OnsetSpreadFrames * 3f);
            var kernel = new float[radius * 2 + 1];
            for (int k = -radius; k <= radius; k++)
                kernel[k + radius] = (float)Math.Exp(-0.5 * k * k / (OnsetSpreadFrames * OnsetSpreadFrames));

            var envelope = new float[frameCount];
            for (int frame = 0; frame < frameCount; frame++)
            {
                float impulse = impulses[frame];
                if (impulse == 0f) continue;

                int from = Math.Max(0, frame - radius);
                int to = Math.Min(frameCount - 1, frame + radius);
                for (int target = from; target <= to; target++)
                    envelope[target] += impulse * kernel[target - frame + radius];
            }

            double sum = 0.0;
            double sumSquares = 0.0;
            for (int frame = 0; frame < frameCount; frame++)
            {
                sum += envelope[frame];
                sumSquares += (double)envelope[frame] * envelope[frame];
            }

            double mean = sum / frameCount;
            double variance = sumSquares / frameCount - mean * mean;
            if (variance <= 0.0) return null;

            float inverseDeviation = (float)(1.0 / Math.Sqrt(variance));
            for (int frame = 0; frame < frameCount; frame++)
                envelope[frame] *= inverseDeviation;

            return envelope;
        }

        /// <summary>
        /// The DP itself: each frame's score is its onset strength plus the best predecessor
        /// one beat back, minus a log-squared penalty for straying from the period. The beat
        /// sequence is read back from the best-scoring frame within the last beat.
        /// </summary>
        private static List<int> _trackFrames(float[] envelope, float periodFrames, int minimumLag, int maximumLag)
        {
            int frameCount = envelope.Length;

            var lagCost = new float[maximumLag + 1];
            for (int lag = minimumLag; lag <= maximumLag; lag++)
            {
                double deviation = Math.Log(lag / periodFrames);
                lagCost[lag] = (float)(Tightness * deviation * deviation);
            }

            var score = new float[frameCount];
            var backlink = new int[frameCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                float best = float.MinValue;
                int bestPrevious = -1;

                int lastLag = Math.Min(maximumLag, frame);
                for (int lag = minimumLag; lag <= lastLag; lag++)
                {
                    float candidate = score[frame - lag] - lagCost[lag];
                    if (candidate > best)
                    {
                        best = candidate;
                        bestPrevious = frame - lag;
                    }
                }

                score[frame] = envelope[frame] + (bestPrevious >= 0 ? best : 0f);
                backlink[frame] = bestPrevious;
            }

            int last = frameCount - 1;
            for (int frame = Math.Max(0, frameCount - maximumLag); frame < frameCount; frame++)
            {
                if (score[frame] > score[last]) last = frame;
            }

            var frames = new List<int>();
            for (int frame = last; frame >= 0; frame = backlink[frame])
                frames.Add(frame);

            frames.Reverse();
            return frames;
        }
    }
}
