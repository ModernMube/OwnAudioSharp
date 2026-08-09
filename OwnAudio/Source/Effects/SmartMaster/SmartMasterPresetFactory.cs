using System;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Speaker type presets for different audio systems
    /// </summary>
    public enum SpeakerType
    {
        /// <summary>
        /// Default - No processing, transparent passthrough
        /// </summary>
        Default,

        /// <summary>
        /// HiFi - High-fidelity stereo speakers (bookshelf/tower)
        /// Typical frequency response: 40Hz-20kHz
        /// </summary>
        HiFi,

        /// <summary>
        /// Headphone - Studio/consumer headphones
        /// Typical frequency response: 20Hz-20kHz, no crossover needed
        /// </summary>
        Headphone,

        /// <summary>
        /// Studio - Professional studio monitors
        /// Typical frequency response: 35Hz-20kHz, flat response
        /// </summary>
        Studio,

        /// <summary>
        /// Club - Club/DJ system (1 sub + 2 satellites)
        /// Typical: Sub 30-100Hz, Satellites 100Hz-18kHz
        /// </summary>
        Club,

        /// <summary>
        /// Concert - Medium concert PA system
        /// Typical: Sub 30-80Hz, Mids 80Hz-1kHz, Highs 1kHz-18kHz
        /// </summary>
        Concert
    }

    /// <summary>
    /// Built-in speaker tunings. The graphic EQ carries the voicing and the
    /// parametric EQ is left flat on purpose - those eight bands are the user's
    /// room tool, the same split a DriveRack works with.
    /// </summary>
    public static class SmartMasterPresetFactory
    {
        /// <summary>
        /// Create a preset configuration for the specified speaker type
        /// </summary>
        public static SmartMasterConfig CreatePreset(SpeakerType speakerType)
        {
            return speakerType switch
            {
                SpeakerType.Default => CreateDefaultPreset(),
                SpeakerType.HiFi => CreateHiFiPreset(),
                SpeakerType.Headphone => CreateHeadphonePreset(),
                SpeakerType.Studio => CreateStudioPreset(),
                SpeakerType.Club => CreateClubPreset(),
                SpeakerType.Concert => CreateConcertPreset(),
                _ => CreateDefaultPreset()
            };
        }

        /// <summary>
        /// Transparent passthrough, only a safety limiter on the way out.
        /// </summary>
        private static SmartMasterConfig CreateDefaultPreset()
        {
            return new SmartMasterConfig
            {
                SubsonicEnabled = false,
                SubharmonicEnabled = false,
                CompressorEnabled = false,
                CrossoverEnabled = false,
                CrossoverFrequency = 80.0f,
                LimiterThreshold = -1.0f,
                LimiterCeiling = -0.3f,
                LimiterRelease = 100.0f
            };
        }

        /// <summary>
        /// Bookshelf or tower pair: a touch of low extension, gentle levelling,
        /// nothing that draws attention to itself.
        /// </summary>
        private static SmartMasterConfig CreateHiFiPreset()
        {
            var config = new SmartMasterConfig
            {
                SubsonicEnabled = true,
                SubsonicFrequency = 28.0f,

                SubharmonicEnabled = true,
                SubharmonicMix = 0.12f,
                SubharmonicLowLevel = 0.8f,
                SubharmonicHighLevel = 1.0f,

                CompressorEnabled = true,
                CompressorThreshold = 0.158f,
                CompressorRatio = 2.0f,
                CompressorAttack = 20.0f,
                CompressorRelease = 250.0f,
                CompressorKnee = 8.0f,

                CrossoverEnabled = false,
                CrossoverFrequency = 60.0f,

                LimiterThreshold = -1.0f,
                LimiterCeiling = -0.3f,
                LimiterRelease = 150.0f
            };

            SetEQCurve(config, new float[]
            {
                1.2f,  1.6f,  1.9f,  1.9f,  1.6f,  1.0f,  0.5f,  0.0f,
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f, -0.6f, -0.9f,
               -0.6f, -0.2f,  0.2f,  0.8f,  1.2f,  1.2f
            });

            return config;
        }

        /// <summary>
        /// Headphones: no cabinet to protect, so no subsonic and no sub synth.
        /// Takes the edge off the usual 5-8k peak and adds a little air.
        /// </summary>
        private static SmartMasterConfig CreateHeadphonePreset()
        {
            var config = new SmartMasterConfig
            {
                SubsonicEnabled = false,
                SubharmonicEnabled = false,

                CompressorEnabled = true,
                CompressorThreshold = 0.200f,
                CompressorRatio = 1.8f,
                CompressorAttack = 25.0f,
                CompressorRelease = 250.0f,
                CompressorKnee = 12.0f,

                CrossoverEnabled = false,
                CrossoverFrequency = 40.0f,

                LimiterThreshold = -2.0f,
                LimiterCeiling = -0.5f,
                LimiterRelease = 120.0f
            };

            SetEQCurve(config, new float[]
            {
                0.0f,  0.4f,  1.0f,  1.8f,  1.9f,  1.8f,  1.2f,  0.5f,
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f, -1.8f, -3.4f,
               -3.8f, -3.4f, -1.6f,  0.8f,  1.8f,  2.0f
            });

            return config;
        }

        /// <summary>
        /// Nearfield monitors, flat on purpose. Only a subsonic filter and a
        /// safety limiter - anything else would be lying to you about the mix.
        /// </summary>
        private static SmartMasterConfig CreateStudioPreset()
        {
            return new SmartMasterConfig
            {
                SubsonicEnabled = true,
                SubsonicFrequency = 30.0f,

                SubharmonicEnabled = false,

                CompressorEnabled = true,
                CompressorThreshold = 0.126f,
                CompressorRatio = 1.5f,
                CompressorAttack = 30.0f,
                CompressorRelease = 300.0f,
                CompressorKnee = 12.0f,

                CrossoverEnabled = false,
                CrossoverFrequency = 50.0f,

                LimiterThreshold = -1.0f,
                LimiterCeiling = -0.3f,
                LimiterRelease = 200.0f
            };
        }

        /// <summary>
        /// One sub plus satellites. Crossover runs so the sub gets its own trim
        /// and driver limiter; alignment delays stay at zero until a measurement
        /// fills them in, otherwise they just comb the crossover region.
        /// </summary>
        private static SmartMasterConfig CreateClubPreset()
        {
            var config = new SmartMasterConfig
            {
                SubsonicEnabled = true,
                SubsonicFrequency = 32.0f,

                SubharmonicEnabled = true,
                SubharmonicMix = 0.25f,
                SubharmonicLowLevel = 1.0f,
                SubharmonicHighLevel = 0.8f,

                CompressorEnabled = true,
                CompressorThreshold = 0.251f,
                CompressorRatio = 3.0f,
                CompressorAttack = 15.0f,
                CompressorRelease = 120.0f,
                CompressorKnee = 6.0f,

                CrossoverEnabled = true,
                CrossoverFrequency = 90.0f,

                MainLimiterThreshold = -3.0f,
                SubLimiterThreshold = -3.0f,

                LimiterThreshold = -1.5f,
                LimiterCeiling = -0.3f,
                LimiterRelease = 60.0f
            };

            SetEQCurve(config, new float[]
            {
                1.5f,  3.2f,  4.2f,  4.4f,  4.2f,  3.4f,  2.0f,  0.8f,
                0.0f,  0.0f, -1.0f, -2.0f, -2.2f, -2.2f, -1.6f, -0.6f,
                0.0f,  0.0f,  0.0f,  0.0f,  1.0f,  2.2f,  2.5f,  2.5f,
                1.8f,  0.6f,  1.0f,  2.2f,  2.5f,  2.5f
            });

            config.OutputGains[0] = 0.0f;
            config.OutputGains[1] = 0.0f;
            config.OutputGains[2] = 1.5f;

            return config;
        }

        /// <summary>
        /// Medium PA: vocal presence up, stage boom out, top end pulled back so
        /// it stays listenable for a whole set.
        /// </summary>
        private static SmartMasterConfig CreateConcertPreset()
        {
            var config = new SmartMasterConfig
            {
                SubsonicEnabled = true,
                SubsonicFrequency = 35.0f,

                SubharmonicEnabled = true,
                SubharmonicMix = 0.18f,
                SubharmonicLowLevel = 0.9f,
                SubharmonicHighLevel = 1.0f,

                CompressorEnabled = true,
                CompressorThreshold = 0.200f,
                CompressorRatio = 2.5f,
                CompressorAttack = 20.0f,
                CompressorRelease = 150.0f,
                CompressorKnee = 8.0f,

                CrossoverEnabled = true,
                CrossoverFrequency = 80.0f,

                MainLimiterThreshold = -3.0f,
                SubLimiterThreshold = -2.0f,

                LimiterThreshold = -2.0f,
                LimiterCeiling = -0.3f,
                LimiterRelease = 80.0f
            };

            SetEQCurve(config, new float[]
            {
               -1.0f, -0.5f,  0.0f,  0.5f,  1.2f,  2.0f,  2.2f,  2.0f,
                1.0f, -1.0f, -2.4f, -3.1f, -3.1f, -2.4f, -1.2f,  0.0f,
                0.0f,  0.8f,  2.0f,  2.8f,  2.8f,  2.6f,  1.6f,  0.5f,
                0.0f, -0.8f, -2.0f, -2.2f, -2.2f, -2.2f
            });

            config.OutputGains[0] = 0.0f;
            config.OutputGains[1] = 0.0f;
            config.OutputGains[2] = 1.0f;

            return config;
        }

        /// <summary>
        /// Helper to set EQ curve from array
        /// </summary>
        private static void SetEQCurve(SmartMasterConfig config, float[] gains)
        {
            int count = Math.Min(gains.Length, SmartMasterConfig.EqBands);
            for (int i = 0; i < count; i++)
            {
                config.GraphicEQGains[i] = gains[i];
            }
        }

        /// <summary>
        /// Get preset filename for speaker type
        /// </summary>
        public static string GetPresetFilename(SpeakerType speakerType)
        {
            return speakerType.ToString().ToLowerInvariant() + ".smartmaster.json";
        }
    }
}
