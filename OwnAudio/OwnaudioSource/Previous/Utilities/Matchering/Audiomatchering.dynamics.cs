using System;
using System.Linq;

namespace OwnaudioLegacy.Utilities.Matchering
{
    /// <summary>
    /// Provides audio spectrum analysis and EQ matching functionality for audio processing applications.
    /// Implements advanced FFT-based frequency analysis and intelligent EQ adjustment algorithms.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region Dynamic Amplification Settings

        /// <summary>
        /// Calculates optimal dynamic amplification settings based on source and target audio characteristics.
        /// Uses conservative approach to preserve musical dynamics while achieving loudness matching.
        /// </summary>
        /// <param name="source">Source audio spectrum analysis</param>
        /// <param name="target">Target audio spectrum analysis</param>
        /// <returns>Optimized dynamic amplification settings</returns>
        private DynamicAmpSettings CalculateDynamicAmpSettings(AudioSpectrum source, AudioSpectrum target)
        {
            float loudnessDifference = target.Loudness - source.Loudness;
            float dynamicDifference = target.DynamicRange - source.DynamicRange;

            float adjustedLoudnessDifference = loudnessDifference * 0.4f;
            float dynamicAdjustment = dynamicDifference * 0.2f;

            return new DynamicAmpSettings
            {
                TargetLevel = -13.0f + Math.Max(-4f, Math.Min(4f, adjustedLoudnessDifference + dynamicAdjustment)),
                AttackTime = 0.25f,
                ReleaseTime = 1.8f,
                MaxGain = 3.5f
            };
        }

        #endregion
    }
}
