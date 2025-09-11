using System;
using System.Linq;

namespace Ownaudio.Utilities.Matchering
{
    /// <summary>
    /// Provides audio spectrum analysis and EQ matching functionality for audio processing applications.
    /// Implements advanced FFT-based frequency analysis and intelligent EQ adjustment algorithms.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region Audio Dynamics Analysis

        /// <summary>
        /// Analyzes comprehensive audio dynamics including RMS, peak levels, loudness, and dynamic range.
        /// </summary>
        /// <param name="audioData">Audio sample data to analyze</param>
        /// <returns>Complete dynamics analysis information</returns>
        private DynamicsInfo AnalyzeDynamics(float[] audioData)
        {
            if (audioData.Length == 0)
                return new DynamicsInfo();

            double sumSquares = audioData.Sum(sample => sample * sample);
            float rms = (float)Math.Sqrt(sumSquares / audioData.Length);

            float peak = audioData.Max(sample => Math.Abs(sample));

            float loudness = 20 * (float)Math.Log10(Math.Max(rms, 1e-10f)) - 23.0f;

            var sortedLevels = audioData.Select(Math.Abs).OrderByDescending(x => x).ToArray();
            int top10Percent = Math.Max(1, sortedLevels.Length / 10);
            int top90Percent = Math.Max(1, sortedLevels.Length * 9 / 10);

            float dynamicRange = 20 * (float)Math.Log10(
                Math.Max(sortedLevels.Take(top10Percent).Average(), 1e-10f) /
                Math.Max(sortedLevels.Take(top90Percent).Average(), 1e-10f));

            return new DynamicsInfo
            {
                RMS = rms,
                Peak = peak,
                Loudness = loudness,
                DynamicRange = Math.Max(0, dynamicRange)
            };
        }

        #endregion

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
