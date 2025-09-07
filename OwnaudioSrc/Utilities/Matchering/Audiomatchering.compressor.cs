using System;

namespace Ownaudio.Utilities.Matchering
{
    public partial class AudioAnalyzer
    {
        /// <summary>
        /// Calculates multiband compression settings for 4 frequency bands
        /// </summary>
        /// <param name="source">Source audio spectrum to be processed</param>
        /// <param name="target">Target audio spectrum to match</param>
        /// <returns>Array of compression settings for each frequency band</returns>
        private CompressionSettings[] CalculateMultibandCompressionSettings(AudioSpectrum source, AudioSpectrum target)
        {
            var settings = new CompressionSettings[4]; // 4 bands

            float dynamicDifference = target.DynamicRange - source.DynamicRange;

            // Base settings
            var baseSettings = CalculateCompressionSettings(source, target);

            for (int i = 0; i < 4; i++)
            {
                settings[i] = new CompressionSettings
                {
                    Threshold = baseSettings.Threshold + GetBandThresholdOffset(i),
                    Ratio = baseSettings.Ratio + GetBandRatioOffset(i),
                    AttackTime = baseSettings.AttackTime * GetBandAttackMultiplier(i),
                    ReleaseTime = baseSettings.ReleaseTime * GetBandReleaseMultiplier(i),
                    MakeupGain = baseSettings.MakeupGain * GetBandMakeupMultiplier(i)
                };
            }

            return settings;
        }

        /// <summary>
        /// Calculates base compressor settings based on source and target dynamics
        /// </summary>
        /// <param name="source">Source audio spectrum</param>
        /// <param name="target">Target audio spectrum</param>
        /// <returns>Base compression settings used as template for multiband processing</returns>
        private CompressionSettings CalculateCompressionSettings(AudioSpectrum source, AudioSpectrum target)
        {
            float dynamicDifference = target.DynamicRange - source.DynamicRange;

            // If target is less dynamic, compression is needed
            if (dynamicDifference < -3.0f)
            {
                return new CompressionSettings
                {
                    Threshold = -8.0f,
                    Ratio = 4.0f,
                    AttackTime = 10.0f,
                    ReleaseTime = 100.0f,
                    MakeupGain = Math.Abs(dynamicDifference) * 0.5f
                };
            }

            return new CompressionSettings
            {
                Threshold = -12.0f,
                Ratio = 2.0f,
                AttackTime = 20.0f,
                ReleaseTime = 200.0f,
                MakeupGain = 0.0f
            };
        }

        /// <summary>
        /// Gets the threshold offset for a specific frequency band
        /// </summary>
        /// <param name="band">Frequency band index (0-3)</param>
        /// <returns>Threshold adjustment in dB for the specified band</returns>
        private float GetBandThresholdOffset(int band)
        {
            // Band-specific threshold adjustments
            return band switch
            {
                0 => 2.0f,  // Low: higher threshold
                1 => 0.0f,  // Low-mid: baseline
                2 => -1.0f, // High-mid: lower threshold
                3 => -2.0f, // High: lowest threshold
                _ => 0.0f
            };
        }

        /// <summary>
        /// Gets the compression ratio offset for a specific frequency band
        /// </summary>
        /// <param name="band">Frequency band index (0-3)</param>
        /// <returns>Ratio adjustment for the specified band</returns>
        private float GetBandRatioOffset(int band)
        {
            return band switch
            {
                0 => -0.5f, // Low: gentler ratio
                1 => 0.0f,  // Low-mid: baseline
                2 => 0.5f,  // High-mid: stronger ratio
                3 => 1.0f,  // High: strongest ratio
                _ => 0.0f
            };
        }

        /// <summary>
        /// Gets the attack time multiplier for a specific frequency band
        /// </summary>
        /// <param name="band">Frequency band index (0-3)</param>
        /// <returns>Attack time multiplier for the specified band</returns>
        private float GetBandAttackMultiplier(int band)
        {
            return band switch
            {
                0 => 2.0f,  // Low: slower attack
                1 => 1.0f,  // Low-mid: baseline
                2 => 0.5f,  // High-mid: faster attack
                3 => 0.3f,  // High: fastest attack
                _ => 1.0f
            };
        }

        /// <summary>
        /// Gets the release time multiplier for a specific frequency band
        /// </summary>
        /// <param name="band">Frequency band index (0-3)</param>
        /// <returns>Release time multiplier for the specified band</returns>
        private float GetBandReleaseMultiplier(int band)
        {
            return band switch
            {
                0 => 1.5f,  // Low: slower release
                1 => 1.0f,  // Low-mid: baseline
                2 => 0.8f,  // High-mid: faster release
                3 => 0.6f,  // High: fastest release
                _ => 1.0f
            };
        }

        /// <summary>
        /// Gets the makeup gain multiplier for a specific frequency band
        /// </summary>
        /// <param name="band">Frequency band index (0-3)</param>
        /// <returns>Makeup gain multiplier for the specified band</returns>
        private float GetBandMakeupMultiplier(int band)
        {
            return band switch
            {
                0 => 0.8f,  // Low: less makeup gain
                1 => 1.0f,  // Low-mid: baseline
                2 => 1.0f,  // High-mid: baseline
                3 => 0.9f,  // High: slightly less makeup
                _ => 1.0f
            };
        }
    }
}
