using System;
using Logger;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// Dynamics side of the matching - AGC target and compressor settings.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region Dynamic Amplification Settings

        /// <summary>
        /// AGC settings. We just chase the target loudness, the compressor below
        /// takes care of the crest factor.
        /// </summary>
        private DynamicAmpSettings _ampSettings(AudioSpectrum source, AudioSpectrum target)
        {
            float sourceCrest = 20 * (float)Math.Log10(source.PeakLevel / Math.Max(source.RMSLevel, 1e-10f));
            float targetCrest = 20 * (float)Math.Log10(target.PeakLevel / Math.Max(target.RMSLevel, 1e-10f));

            Log.Info($"Dynamics Match: Source Crest {sourceCrest:F1}dB vs Target {targetCrest:F1}dB");

            return new DynamicAmpSettings
            {
                TargetLevel = target.Loudness,
                AttackTime = 0.1f,
                ReleaseTime = 0.5f,
                MaxGain = 6.0f
            };
        }

        /// <summary>
        /// Compressor threshold/ratio derived from the crest difference. If the source is
        /// already tighter than the target we only catch the top peaks.
        /// </summary>
        private (float Threshold, float Ratio) _compSettings(AudioSpectrum source, AudioSpectrum target)
        {
            float sourceCrest = 20 * (float)Math.Log10(source.PeakLevel / Math.Max(source.RMSLevel, 1e-10f));
            float targetCrest = 20 * (float)Math.Log10(target.PeakLevel / Math.Max(target.RMSLevel, 1e-10f));

            float crestDiff = sourceCrest - targetCrest;
            float ratio, threshold;

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

            Log.Info($"Calculated Compressor: Thresh {threshold:F1}dB, Ratio {ratio:F1}:1");

            return (Math.Clamp(threshold, -40f, -0.5f), Math.Clamp(ratio, 1.2f, 6.0f));
        }

        #endregion
    }
}
