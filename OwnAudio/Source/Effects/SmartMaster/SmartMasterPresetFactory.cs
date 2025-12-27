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
    /// Factory for creating speaker-specific presets
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
        /// Default - Transparent passthrough, no processing
        /// </summary>
        private static SmartMasterConfig CreateDefaultPreset()
        {
            var config = new SmartMasterConfig
            {
                // All processing disabled
                SubharmonicEnabled = false,
                CompressorEnabled = false,
                
                // Neutral settings
                CrossoverFrequency = 80.0f,
                LimiterThreshold = -0.3f, // dB - transparent limiting, only catches peaks
                LimiterRelease = 50.0f
            };
            
            // Flat EQ (all 0 dB)
            for (int i = 0; i < 31; i++)
            {
                config.GraphicEQGains[i] = 0.0f;
            }
            
            return config;
        }
        
        /// <summary>
        /// HiFi - High-fidelity stereo speakers
        /// Typical bookshelf/tower speakers with good bass extension
        /// </summary>
        private static SmartMasterConfig CreateHiFiPreset()
        {
            var config = new SmartMasterConfig
            {
                // Minimal subharmonic enhancement for natural bass
                SubharmonicEnabled = true,
                SubharmonicMix = 0.15f,
                SubharmonicFreqRange = 50.0f,
                
                // Light compression for consistent dynamics
                CompressorEnabled = true,
                CompressorThreshold = 0.85f,
                CompressorRatio = 2.5f,
                CompressorAttack = 15.0f,
                CompressorRelease = 150.0f,
                
                // No crossover needed for full-range speakers
                CrossoverFrequency = 40.0f,
                
                // Conservative limiter
                LimiterThreshold = -0.1f, // dB
                LimiterRelease = 100.0f
            };
            
            // Slight bass boost and presence enhancement
            SetEQCurve(config, new float[]
            {
                1.5f,  1.2f,  0.8f,  0.5f,  0.3f,  0.2f,  0.0f,  0.0f,  // 20-160Hz: gentle bass lift
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  // 200-1.6kHz: flat
                0.0f,  0.0f,  0.0f,  0.5f,  0.8f,  1.0f,  0.8f,  0.5f,  // 2k-8kHz: presence boost
                0.3f,  0.2f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f           // 10k-20kHz: gentle rolloff
            });
            
            return config;
        }
        
        /// <summary>
        /// Headphone - Studio/consumer headphones
        /// No crossover, focus on balanced response
        /// </summary>
        private static SmartMasterConfig CreateHeadphonePreset()
        {
            var config = new SmartMasterConfig
            {
                // No subharmonic needed for headphones
                SubharmonicEnabled = false,
                
                // Gentle compression for comfort
                CompressorEnabled = true,
                CompressorThreshold = 0.90f,
                CompressorRatio = 2.0f,
                CompressorAttack = 20.0f,
                CompressorRelease = 200.0f,
                
                // No crossover for headphones
                CrossoverFrequency = 20.0f,
                
                // Protective limiter
                LimiterThreshold = -0.5f, // dB
                LimiterRelease = 80.0f
            };
            
            // Compensate for typical headphone response
            SetEQCurve(config, new float[]
            {
                0.5f,  0.3f,  0.2f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  // 20-160Hz: slight bass
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  // 200-1.6kHz: flat
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  // 2k-8kHz: flat
                -0.5f, -0.8f, -1.0f, -0.8f, -0.5f, -0.3f, 0.0f          // 10k-20kHz: reduce harshness
            });
            
            return config;
        }
        
        /// <summary>
        /// Studio - Professional studio monitors
        /// Flat response, minimal processing
        /// </summary>
        private static SmartMasterConfig CreateStudioPreset()
        {
            var config = new SmartMasterConfig
            {
                // Minimal subharmonic for extended low end
                SubharmonicEnabled = true,
                SubharmonicMix = 0.10f,
                SubharmonicFreqRange = 40.0f,
                
                // Very light compression
                CompressorEnabled = true,
                CompressorThreshold = 0.85f,
                CompressorRatio = 1.5f,
                CompressorAttack = 25.0f,
                CompressorRelease = 250.0f,
                
                // Low crossover for extended bass
                CrossoverFrequency = 35.0f,
                
                // Transparent limiter
                LimiterThreshold = -0.2f, // dB
                LimiterRelease = 150.0f
            };
            
            // Nearly flat EQ with minimal room compensation
            SetEQCurve(config, new float[]
            {
                0.3f,  0.2f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  // 20-160Hz: minimal bass
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  // 200-1.6kHz: flat
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  // 2k-8kHz: flat
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f           // 10k-20kHz: flat
            });
            
            return config;
        }
        
        /// <summary>
        /// Club - DJ/Club system (1 sub + 2 satellites)
        /// Heavy bass, crossover at 100Hz
        /// </summary>
        private static SmartMasterConfig CreateClubPreset()
        {
            var config = new SmartMasterConfig
            {
                // Strong subharmonic for club bass
                SubharmonicEnabled = true,
                SubharmonicMix = 0.40f,
                SubharmonicFreqRange = 70.0f,
                
                // Moderate compression for consistent loudness
                CompressorEnabled = true,
                CompressorThreshold = 0.75f,
                CompressorRatio = 3.5f,
                CompressorAttack = 10.0f,
                CompressorRelease = 100.0f,
                
                // Crossover at 100Hz for sub/satellite split
                CrossoverFrequency = 100.0f,
                
                // Aggressive limiter for protection
                LimiterThreshold = -0.7f, // dB
                LimiterRelease = 40.0f
            };
            
            // Club EQ: boosted bass and highs
            SetEQCurve(config, new float[]
            {
                4.0f,  3.5f,  3.0f,  2.5f,  2.0f,  1.5f,  1.0f,  0.5f,  // 20-160Hz: heavy bass
                0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  // 200-1.6kHz: flat
                0.5f,  1.0f,  1.5f,  2.0f,  2.5f,  2.0f,  1.5f,  1.0f,  // 2k-8kHz: presence
                0.5f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f           // 10k-20kHz: controlled highs
            });
            
            // Typical club system delays (sub slightly delayed)
            config.TimeDelays[0] = 0.0f;  // Left
            config.TimeDelays[1] = 0.0f;  // Right
            config.TimeDelays[2] = 2.0f;  // Sub (2ms delay for alignment)
            
            return config;
        }
        
        /// <summary>
        /// Concert - Medium concert PA system
        /// Multi-way system with sub, mids, highs
        /// </summary>
        private static SmartMasterConfig CreateConcertPreset()
        {
            var config = new SmartMasterConfig
            {
                // Moderate subharmonic for concert bass
                SubharmonicEnabled = true,
                SubharmonicMix = 0.30f,
                SubharmonicFreqRange = 60.0f,
                
                // Compression for consistent PA output
                CompressorEnabled = true,
                CompressorThreshold = 0.80f,
                CompressorRatio = 3.0f,
                CompressorAttack = 12.0f,
                CompressorRelease = 120.0f,
                
                // Crossover at 80Hz for sub/main split
                CrossoverFrequency = 80.0f,
                
                // Protective limiter for PA system
                LimiterThreshold = -0.6f, // dB
                LimiterRelease = 50.0f
            };
            
            // Concert EQ: compensate for typical PA response
            SetEQCurve(config, new float[]
            {
                3.0f,  2.5f,  2.0f,  1.5f,  1.0f,  0.5f,  0.0f,  0.0f,  // 20-160Hz: bass boost
                0.0f,  0.0f,  0.0f, -0.5f, -0.8f, -0.5f,  0.0f,  0.0f,  // 200-1.6kHz: slight mid scoop
                0.5f,  1.0f,  1.5f,  2.0f,  2.5f,  2.0f,  1.5f,  1.0f,  // 2k-8kHz: vocal presence
                0.5f,  0.0f, -0.5f, -1.0f, -1.5f, -1.0f, -0.5f          // 10k-20kHz: air control
            });
            
            // Typical concert system delays
            config.TimeDelays[0] = 0.0f;  // Left
            config.TimeDelays[1] = 0.0f;  // Right
            config.TimeDelays[2] = 3.0f;  // Sub (3ms delay for alignment)
            
            return config;
        }
        
        /// <summary>
        /// Helper to set EQ curve from array
        /// </summary>
        private static void SetEQCurve(SmartMasterConfig config, float[] gains)
        {
            int count = Math.Min(gains.Length, 31);
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
