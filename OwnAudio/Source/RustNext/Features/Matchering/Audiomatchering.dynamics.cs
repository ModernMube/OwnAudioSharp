using System;
using System.Linq;
using Logger;

namespace OwnaudioNET.RustNext.Features.Matchering
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
        /// <summary>
        /// Calculates comprehensive dynamic settings (Amp + Compressor) based on Crest Factor analysis.
        /// </summary>
        private DynamicAmpSettings CalculateDynamicAmpSettings(AudioSpectrum source, AudioSpectrum target)
        {
            float sourceCrest = 20 * (float)Math.Log10(source.PeakLevel / Math.Max(source.RMSLevel, 1e-10f));
            float targetCrest = 20 * (float)Math.Log10(target.PeakLevel / Math.Max(target.RMSLevel, 1e-10f));

            float targetLoudnessDb = target.Loudness;

            float crestDiff = sourceCrest - targetCrest;

            float compThreshold = -12.0f;
            float compRatio = 1.0f;

            if (crestDiff > 0)
            {
                compRatio = 1.5f + (crestDiff * 0.5f); // e.g., 6dB diff -> 1.5 + 3 = 4.5 ratio
                compThreshold = source.Loudness + (sourceCrest - crestDiff) * 0.5f;
            }
            else
            {
                compRatio = 1.5f;
                compThreshold = source.PeakLevel - 6.0f; // Just catch highest peaks
            }

            compRatio = Math.Clamp(compRatio, 1.0f, 10.0f);
            compThreshold = Math.Clamp(compThreshold, -30.0f, -2.0f);

            Log.Info($"Dynamics Match: Source Crest {sourceCrest:F1}dB vs Target {targetCrest:F1}dB");
            Log.Info($"Calculated Compressor: Thresh {compThreshold:F1}dB, Ratio {compRatio:F1}:1");

            return new DynamicAmpSettings
            {
                TargetLevel = targetLoudnessDb, // Match loudness directly
                AttackTime = 0.1f, // Faster for modern sound
                ReleaseTime = 0.5f,
                MaxGain = 6.0f     // Allow more gain if needed
            };
        }

        /// <summary>
        /// Calculates optimal compressor settings to match target dynamics.
        /// </summary>
        private (float Threshold, float Ratio) CalculateCompressorSettings(AudioSpectrum source, AudioSpectrum target)
        {
            float sourceCrest = 20 * (float)Math.Log10(source.PeakLevel / Math.Max(source.RMSLevel, 1e-10f));
            float targetCrest = 20 * (float)Math.Log10(target.PeakLevel / Math.Max(target.RMSLevel, 1e-10f));

            float crestDiff = sourceCrest - targetCrest;
            float ratio = 1.0f;
            float threshold = -0.1f;

            if (crestDiff > 1.0f)
            {
                ratio = 1.0f + (crestDiff * 0.15f);
                threshold = 20 * (float)Math.Log10(source.RMSLevel) + 6.0f;
            }
            else
            {
                ratio = 2.0f;
                threshold = 20 * (float)Math.Log10(source.PeakLevel) - 4.0f;
            }

            return (Math.Clamp(threshold, -40f, -0.5f), Math.Clamp(ratio, 1.2f, 6.0f));
        }

        #endregion
    }
}
