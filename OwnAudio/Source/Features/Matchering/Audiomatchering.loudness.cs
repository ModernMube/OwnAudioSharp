using System;
using System.Collections.Generic;
using Logger;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// Loudness side of the analysis, measured the way the OwnDynamicAmp and OwnCompressor
    /// hear the audio: BS.1770 K-weighting, 400 ms momentary blocks, the -70 LUFS absolute and
    /// -10 LU relative gates, EBU 3342 loudness range and the same 4x true-peak interpolator
    /// the rider's limiter runs. Whatever number the match derives from these is a number the
    /// effects will read back the same way.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region Constants

        private const double LufsOffset = -0.691;
        private const float SilentLufs = -120f;
        private const float AbsoluteGateLufs = -70f;
        private const float IntegratedRelativeGateLu = -10f;
        private const float RangeRelativeGateLu = -20f;
        private const double SubBlockSeconds = 0.1;
        private const int MomentarySubBlocks = 4;
        private const int ShortTermSubBlocks = 30;
        private const int TruePeakTaps = 8;
        private const int TruePeakPhases = 3;
        private const int TruePeakDelay = TruePeakTaps / 2;
        private const float MonoSideToMidDb = -60f;

        private static readonly float[][] _truePeakKernel = _buildTruePeakKernel();

        #endregion

        #region Loudness Measurement

        /// <summary>
        /// Integrated loudness, loudness range, true peak, noise floor and stereo width of an
        /// interleaved buffer. Channels are summed with unity weight, as the rider does.
        /// </summary>
        public LoudnessInfo MeasureLoudness(float[] interleaved, int sampleRate, int channels)
        {
            if (interleaved is null || interleaved.Length == 0)
                throw new ArgumentException("There is nothing to measure.", nameof(interleaved));

            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
            if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            List<double> subBlocks = _kWeightedSubBlocks(interleaved, sampleRate, channels);

            List<double> momentary = _slidingPower(subBlocks, MomentarySubBlocks);
            List<double> shortTerm = _slidingPower(subBlocks, ShortTermSubBlocks);

            var info = new LoudnessInfo
            {
                IntegratedLufs = _integratedLufs(momentary),
                LoudnessRangeLu = _loudnessRange(shortTerm),
                TruePeakDbtp = _toDb(_truePeak(interleaved, channels)),
                NoiseFloorLufs = _noiseFloor(momentary),
                SideToMidDb = channels == 2 ? _sideToMidDb(interleaved) : MonoSideToMidDb
            };

            Log.Info($"Loudness: {info.IntegratedLufs:F1} LUFS, LRA {info.LoudnessRangeLu:F1} LU, " +
                     $"true peak {info.TruePeakDbtp:F1} dBTP, PLR {info.PeakToLoudnessDb:F1} dB, " +
                     $"floor {info.NoiseFloorLufs:F1} LUFS, side/mid {info.SideToMidDb:F1} dB");

            return info;
        }

        /// <summary>
        /// Channel summed K-weighted power per 100 ms, the rider's sub-block.
        /// </summary>
        private static List<double> _kWeightedSubBlocks(float[] interleaved, int sampleRate, int channels)
        {
            var (shelf, highPass) = _kWeighting(sampleRate);
            var state = new double[channels, 4];

            int frames = interleaved.Length / channels;
            int subLength = Math.Max(1, (int)Math.Round(sampleRate * SubBlockSeconds));
            var blocks = new List<double>(frames / subLength + 1);

            double sum = 0;
            int count = 0;

            for (int f = 0; f < frames; f++)
            {
                double energy = 0;

                for (int c = 0; c < channels; c++)
                {
                    double x = interleaved[f * channels + c];

                    double y1 = shelf[0] * x + state[c, 0];
                    state[c, 0] = shelf[1] * x - shelf[3] * y1 + state[c, 1];
                    state[c, 1] = shelf[2] * x - shelf[4] * y1;

                    double y2 = highPass[0] * y1 + state[c, 2];
                    state[c, 2] = highPass[1] * y1 - highPass[3] * y2 + state[c, 3];
                    state[c, 3] = highPass[2] * y1 - highPass[4] * y2;

                    energy += y2 * y2;
                }

                sum += energy;

                if (++count == subLength)
                {
                    blocks.Add(sum / subLength);
                    sum = 0;
                    count = 0;
                }
            }

            return blocks;
        }

        /// <summary>
        /// BS.1770-4 shelf and RLB high-pass rebuilt from the analog prototype, the exact
        /// coefficients owndynamicamp.rs uses, as { b0, b1, b2, a1, a2 }.
        /// </summary>
        private static (double[] Shelf, double[] HighPass) _kWeighting(int sampleRate)
        {
            double fs = sampleRate;

            double f0 = 1681.974450955533;
            double g = 3.999843853973347;
            double q = 0.7071752369554196;
            double k = Math.Tan(Math.PI * f0 / fs);
            double vh = Math.Pow(10.0, g / 20.0);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1.0 + k / q + k * k;

            double[] shelf =
            {
                (vh + vb * k / q + k * k) / a0,
                2.0 * (k * k - vh) / a0,
                (vh - vb * k / q + k * k) / a0,
                2.0 * (k * k - 1.0) / a0,
                (1.0 - k / q + k * k) / a0
            };

            f0 = 38.13547087602444;
            q = 0.5003270373238773;
            k = Math.Tan(Math.PI * f0 / fs);
            a0 = 1.0 + k / q + k * k;

            double[] highPass =
            {
                1.0, -2.0, 1.0,
                2.0 * (k * k - 1.0) / a0,
                (1.0 - k / q + k * k) / a0
            };

            return (shelf, highPass);
        }

        /// <summary>
        /// Mean power of every run of `length` sub-blocks, stepping one sub-block at a time.
        /// </summary>
        private static List<double> _slidingPower(List<double> subBlocks, int length)
        {
            var result = new List<double>(Math.Max(0, subBlocks.Count - length + 1));
            if (subBlocks.Count < length) return result;

            double sum = 0;
            for (int i = 0; i < length; i++) sum += subBlocks[i];
            result.Add(sum / length);

            for (int i = length; i < subBlocks.Count; i++)
            {
                sum += subBlocks[i] - subBlocks[i - length];
                result.Add(Math.Max(sum, 0) / length);
            }

            return result;
        }

        private static float _powerToLufs(double power) =>
            power <= 1e-20 ? SilentLufs : (float)(LufsOffset + 10.0 * Math.Log10(power));

        private static double _lufsToPower(float lufs) =>
            Math.Pow(10.0, (lufs - LufsOffset) / 10.0);

        /// <summary>
        /// Gated integrated loudness: absolute gate, then 10 LU under the absolute gated mean.
        /// </summary>
        private static float _integratedLufs(List<double> momentary)
        {
            double absolute = _lufsToPower(AbsoluteGateLufs);

            double sum = 0;
            int count = 0;
            foreach (double p in momentary)
                if (p >= absolute) { sum += p; count++; }

            if (count == 0) return SilentLufs;

            double relative = sum / count * Math.Pow(10.0, IntegratedRelativeGateLu / 10.0);

            double gated = 0;
            int gatedCount = 0;
            foreach (double p in momentary)
                if (p >= absolute && p >= relative) { gated += p; gatedCount++; }

            return gatedCount == 0 ? SilentLufs : _powerToLufs(gated / gatedCount);
        }

        /// <summary>
        /// EBU Tech 3342 loudness range: short-term blocks gated at -70 LUFS and 20 LU under
        /// their mean, then the spread between the 10th and the 95th percentile.
        /// </summary>
        private static float _loudnessRange(List<double> shortTerm)
        {
            double absolute = _lufsToPower(AbsoluteGateLufs);

            double sum = 0;
            int count = 0;
            foreach (double p in shortTerm)
                if (p >= absolute) { sum += p; count++; }

            if (count < 2) return 0f;

            double relative = sum / count * Math.Pow(10.0, RangeRelativeGateLu / 10.0);

            var levels = new List<float>(count);
            foreach (double p in shortTerm)
                if (p >= absolute && p >= relative) levels.Add(_powerToLufs(p));

            if (levels.Count < 2) return 0f;

            levels.Sort();

            return Math.Max(0f, _percentile(levels, 0.95f) - _percentile(levels, 0.10f));
        }

        /// <summary>
        /// Where the quiet parts sit: the 10th percentile of the non-silent momentary blocks.
        /// Fades and pauses land here, which is what the rider's freeze threshold has to clear.
        /// </summary>
        private static float _noiseFloor(List<double> momentary)
        {
            var levels = new List<float>(momentary.Count);
            foreach (double p in momentary)
            {
                float lufs = _powerToLufs(p);
                if (lufs > SilentLufs) levels.Add(lufs);
            }

            if (levels.Count == 0) return SilentLufs;

            levels.Sort();

            return _percentile(levels, 0.10f);
        }

        private static float _percentile(List<float> sorted, float fraction)
        {
            float position = fraction * (sorted.Count - 1);
            int lower = (int)Math.Floor(position);
            int upper = Math.Min(lower + 1, sorted.Count - 1);

            return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
        }

        /// <summary>
        /// 4x oversampled peak through the rider's own windowed sinc kernel, so a ceiling
        /// derived from this is the ceiling its limiter enforces.
        /// </summary>
        private static float _truePeak(float[] interleaved, int channels)
        {
            int frames = interleaved.Length / channels;
            float peak = 0f;

            for (int i = 0; i < frames * channels; i++)
                peak = Math.Max(peak, Math.Abs(interleaved[i]));

            for (int c = 0; c < channels; c++)
            {
                for (int f = TruePeakTaps - 1; f < frames; f++)
                {
                    int first = (f - TruePeakTaps + 1) * channels + c;

                    foreach (float[] phase in _truePeakKernel)
                    {
                        float acc = 0f;
                        for (int j = 0; j < TruePeakTaps; j++)
                            acc += phase[j] * interleaved[first + j * channels];

                        float abs = Math.Abs(acc);
                        if (abs > peak) peak = abs;
                    }
                }
            }

            return peak;
        }

        /// <summary>
        /// Windowed sinc taps for the three in-between phases, as in owndynamicamp.rs.
        /// </summary>
        private static float[][] _buildTruePeakKernel()
        {
            var kernel = new float[TruePeakPhases][];
            double radius = TruePeakTaps / 2.0 + 0.5;

            for (int p = 0; p < TruePeakPhases; p++)
            {
                double frac = (p + 1) / 4.0;
                double[] taps = new double[TruePeakTaps];
                double sum = 0;

                for (int j = 0; j < TruePeakTaps; j++)
                {
                    double d = frac - (j - (TruePeakDelay - 1.0));
                    double x = Math.PI * d;
                    double sinc = Math.Abs(d) < 1e-12 ? 1.0 : Math.Sin(x) / x;
                    double window = 0.5 + 0.5 * Math.Cos(Math.PI * d / radius);

                    taps[j] = sinc * window;
                    sum += taps[j];
                }

                kernel[p] = new float[TruePeakTaps];
                for (int j = 0; j < TruePeakTaps; j++) kernel[p][j] = (float)(taps[j] / sum);
            }

            return kernel;
        }

        /// <summary>
        /// Side energy against mid energy in dB. What the compressor's mid/side mode and its
        /// stereo link get decided on.
        /// </summary>
        private static float _sideToMidDb(float[] interleaved)
        {
            double mid = 0, side = 0;

            for (int i = 0; i + 1 < interleaved.Length; i += 2)
            {
                double m = 0.5 * (interleaved[i] + interleaved[i + 1]);
                double s = 0.5 * (interleaved[i] - interleaved[i + 1]);
                mid += m * m;
                side += s * s;
            }

            if (mid <= 1e-20) return side <= 1e-20 ? MonoSideToMidDb : 20f;

            return Math.Clamp((float)(10.0 * Math.Log10(Math.Max(side, 1e-20) / mid)), MonoSideToMidDb, 20f);
        }

        #endregion
    }
}
