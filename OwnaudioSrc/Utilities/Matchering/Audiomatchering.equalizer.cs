using System;
using System.Collections.Generic;
using System.Linq;

namespace Ownaudio.Utilities.Matchering
{
    partial class AudioAnalyzer
    {
        #region EQ Calculation and Smoothing

        /// <summary>
        /// Calculates intelligent EQ curve with frequency-dependent processing
        /// </summary>
        /// <param name="source">Source audio spectrum</param>
        /// <param name="target">Target audio spectrum</param>
        /// <returns>EQ adjustment values in dB</returns>
        private float[] CalculateEQAdjustments(AudioSpectrum source, AudioSpectrum target)
        {
            var rawAdjustments = new float[FrequencyBands.Length];

            for (int i = 0; i < rawAdjustments.Length; i++)
            {
                float sourceLevel = 20 * (float)Math.Log10(Math.Max(source.FrequencyBands[i], 1e-10f));
                float targetLevel = 20 * (float)Math.Log10(Math.Max(target.FrequencyBands[i], 1e-10f));
                rawAdjustments[i] = targetLevel - sourceLevel;
            }

            var smoothedAdjustments = ApplyIntelligentEQSmoothing(rawAdjustments, source);
            var finalAdjustments = ApplyFrequencySpecificLimiting(smoothedAdjustments);

            return ApplyDistortionPrevention(finalAdjustments, source);
        }

        /// <summary>
        /// Applies multi-pass smoothing with frequency-dependent weights
        /// </summary>
        /// <param name="rawAdjustments">Raw EQ adjustments</param>
        /// <param name="source">Source audio spectrum</param>
        /// <returns>Smoothed adjustments</returns>
        private float[] ApplyIntelligentEQSmoothing(float[] rawAdjustments, AudioSpectrum source)
        {
            var smoothed = new float[rawAdjustments.Length];

            for (int i = 0; i < rawAdjustments.Length; i++)
            {
                smoothed[i] = ApplyAdaptiveSmoothing(rawAdjustments, i);
            }

            for (int i = 0; i < smoothed.Length; i++)
            {
                float freqWeight = GetFrequencyImportanceWeight(FrequencyBands[i]);
                float dynamicWeight = GetDynamicContentWeight(source, i);

                smoothed[i] *= freqWeight * dynamicWeight;
            }

            smoothed = ApplySlopeLimiting(smoothed);

            return smoothed;
        }

        /// <summary>
        /// Applies adaptive smoothing kernel based on frequency content
        /// </summary>
        /// <param name="adjustments">EQ adjustments array</param>
        /// <param name="index">Current band index</param>
        /// <returns>Smoothed value</returns>
        private float ApplyAdaptiveSmoothing(float[] adjustments, int index)
        {
            float frequency = FrequencyBands[index];
            int kernelSize = GetSmoothingKernelSize(frequency);

            float weightedSum = 0;
            float totalWeight = 0;

            int start = Math.Max(0, index - kernelSize);
            int end = Math.Min(adjustments.Length - 1, index + kernelSize);

            for (int i = start; i <= end; i++)
            {
                float distance = Math.Abs(i - index);
                float weight = CalculateSmoothingWeight(distance, kernelSize, frequency);

                weightedSum += adjustments[i] * weight;
                totalWeight += weight;
            }

            return totalWeight > 0 ? weightedSum / totalWeight : adjustments[index];
        }

        /// <summary>
        /// Gets frequency-dependent smoothing kernel size
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <returns>Kernel size</returns>
        private int GetSmoothingKernelSize(float frequency)
        {
            if (frequency < 250) return 2;
            if (frequency < 2000) return 1;
            if (frequency < 8000) return 1;
            return 0;
        }

        /// <summary>
        /// Calculates smoothing weight with frequency dependency
        /// </summary>
        /// <param name="distance">Distance from center</param>
        /// <param name="kernelSize">Size of smoothing kernel</param>
        /// <param name="frequency">Frequency in Hz</param>
        /// <returns>Weight value</returns>
        private float CalculateSmoothingWeight(float distance, int kernelSize, float frequency)
        {
            if (distance == 0) return 1.0f;
            if (distance > kernelSize) return 0;

            float normalizedDistance = distance / kernelSize;
            float baseWeight = (float)Math.Exp(-normalizedDistance * normalizedDistance * 2.0);

            float freqAdjustment = GetFrequencyAdjustmentFactor(frequency);

            return baseWeight * freqAdjustment;
        }

        /// <summary>
        /// Gets frequency adjustment factor for smoothing
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <returns>Adjustment factor</returns>
        private float GetFrequencyAdjustmentFactor(float frequency)
        {
            if (frequency < 100) return 1.2f;
            if (frequency > 8000) return 0.8f;
            return 1.0f;
        }

        /// <summary>
        /// Gets frequency importance weighting for EQ adjustments
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <returns>Importance weight</returns>
        private float GetFrequencyImportanceWeight(float frequency)
        {
            var weights = new Dictionary<(float min, float max), float>
            {
                { (20, 80), 0.8f },
                { (80, 250), 0.9f },
                { (250, 500), 1.0f },
                { (500, 2000), 1.1f },
                { (2000, 5000), 1.0f },
                { (5000, 10000), 0.9f },
                { (10000, 20000), 0.7f }
            };

            foreach (var range in weights)
            {
                if (frequency >= range.Key.min && frequency <= range.Key.max)
                    return range.Value;
            }

            return 0.8f;
        }

        /// <summary>
        /// Gets dynamic content weighting based on source material
        /// </summary>
        /// <param name="source">Source audio spectrum</param>
        /// <param name="bandIndex">Band index</param>
        /// <returns>Dynamic weight</returns>
        private float GetDynamicContentWeight(AudioSpectrum source, int bandIndex)
        {
            float bandEnergy = source.FrequencyBands[bandIndex];
            float avgEnergy = source.FrequencyBands.Average();

            if (bandEnergy > avgEnergy * 1.5f)
                return 0.7f;

            if (bandEnergy < avgEnergy * 0.5f)
                return 1.2f;

            return 1.0f;
        }

        /// <summary>
        /// Applies slope limiting to prevent unnatural EQ curves
        /// </summary>
        /// <param name="adjustments">EQ adjustments</param>
        /// <returns>Slope-limited adjustments</returns>
        private float[] ApplySlopeLimiting(float[] adjustments)
        {
            var limited = new float[adjustments.Length];
            limited[0] = adjustments[0];

            for (int i = 1; i < adjustments.Length; i++)
            {
                float maxSlope = GetMaxAllowedSlope(FrequencyBands[i - 1], FrequencyBands[i]);
                float actualSlope = adjustments[i] - limited[i - 1];

                if (Math.Abs(actualSlope) > maxSlope)
                {
                    limited[i] = limited[i - 1] + Math.Sign(actualSlope) * maxSlope;
                }
                else
                {
                    limited[i] = adjustments[i];
                }
            }

            return limited;
        }

        /// <summary>
        /// Gets maximum allowed slope between frequency bands
        /// </summary>
        /// <param name="freq1">First frequency</param>
        /// <param name="freq2">Second frequency</param>
        /// <returns>Maximum slope in dB</returns>
        private float GetMaxAllowedSlope(float freq1, float freq2)
        {
            float octaveDistance = (float)Math.Log2(freq2 / freq1);

            if (freq1 < 500 && freq2 > 500) return 6.0f;
            if (freq1 < 2000 && freq2 > 2000) return 8.0f;

            return 12.0f * octaveDistance;
        }

        /// <summary>
        /// Applies frequency-specific limiting with distortion prevention
        /// </summary>
        /// <param name="adjustments">Raw EQ adjustments</param>
        /// <returns>Limited adjustments</returns>
        private float[] ApplyFrequencySpecificLimiting(float[] adjustments)
        {
            var limited = new float[adjustments.Length];

            for (int i = 0; i < adjustments.Length; i++)
            {
                float frequency = FrequencyBands[i];
                float maxBoost = GetMaxBoostForFrequency(frequency);
                float maxCut = GetMaxCutForFrequency(frequency);

                limited[i] = Math.Max(-maxCut, Math.Min(maxBoost, adjustments[i]));
            }

            return limited;
        }

        /// <summary>
        /// Gets frequency-dependent boost limits
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <returns>Maximum boost in dB</returns>
        private float GetMaxBoostForFrequency(float frequency)
        {
            if (frequency < 60) return 4.0f;
            if (frequency < 250) return 6.0f;
            if (frequency < 500) return 8.0f;
            if (frequency < 2000) return 10.0f;
            if (frequency < 5000) return 8.0f;
            if (frequency < 10000) return 6.0f;
            return 4.0f;
        }

        /// <summary>
        /// Gets frequency-dependent cut limits
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <returns>Maximum cut in dB</returns>
        private float GetMaxCutForFrequency(float frequency)
        {
            if (frequency < 60) return 8.0f;
            if (frequency < 250) return 12.0f;
            if (frequency > 10000) return 10.0f;
            return 15.0f;
        }

        /// <summary>
        /// Applies distortion prevention based on headroom analysis
        /// </summary>
        /// <param name="adjustments">EQ adjustments</param>
        /// <param name="source">Source audio spectrum</param>
        /// <returns>Distortion-safe adjustments</returns>
        private float[] ApplyDistortionPrevention(float[] adjustments, AudioSpectrum source)
        {
            float availableHeadroom = CalculateAvailableHeadroom(source);
            float totalBoost = adjustments.Where(x => x > 0).Sum();

            if (totalBoost > availableHeadroom * 2.0f)
            {
                float scaleFactor = (availableHeadroom * 2.0f) / totalBoost;

                for (int i = 0; i < adjustments.Length; i++)
                {
                    if (adjustments[i] > 0)
                    {
                        adjustments[i] *= scaleFactor;
                    }
                }

                Console.WriteLine($"Applied distortion prevention: scaled boosts by {scaleFactor:F2}");
            }

            return adjustments;
        }

        /// <summary>
        /// Calculates available headroom for safe boosting
        /// </summary>
        /// <param name="source">Source audio spectrum</param>
        /// <returns>Available headroom in dB</returns>
        private float CalculateAvailableHeadroom(AudioSpectrum source)
        {
            float crestFactor = 20 * (float)Math.Log10(source.PeakLevel / Math.Max(source.RMSLevel, 1e-10f));
            float dynamicHeadroom = Math.Max(3.0f, 20.0f - crestFactor);

            float loudnessHeadroom = Math.Max(0, -9.0f - source.Loudness);

            return Math.Min(dynamicHeadroom, loudnessHeadroom);
        }

        #endregion
    }
}
