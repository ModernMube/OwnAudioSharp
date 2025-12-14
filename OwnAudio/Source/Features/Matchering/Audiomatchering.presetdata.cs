using System;
using System.Collections.Generic;

namespace OwnaudioNET.Features.Matchering
{
    partial class AudioAnalyzer
    {
        /// <summary>
        /// Gets available playback system presets
        /// </summary>
        /// <returns>Dictionary of available presets with their configurations</returns>
        public static Dictionary<PlaybackSystem, PlaybackPreset> GetAvailablePresets()
        {
            return new Dictionary<PlaybackSystem, PlaybackPreset>(SystemPresets);
        }

        /// <summary>
        /// Predefined preset configurations for different playback systems
        /// </summary>
        private static readonly Dictionary<PlaybackSystem, PlaybackPreset> SystemPresets =  new Dictionary<PlaybackSystem, PlaybackPreset>{
            [PlaybackSystem.ConcertPA] = new PlaybackPreset
            {
                Name = "Concert PA System",
                Description = "Large venue sound reinforcement with extended dynamics",
                FrequencyResponse = new float[]
            {
                // 20-16kHz: Cleaner low-end
                -3f, -2f, -1f, 0f, +1f, +1f, +0.5f, 0f, 0f, 0f,    // 20-160Hz: Tighter bass
                +0.5f, 0.5f, 0f, 0f, 0f, -0.5f, -0.5f, 0f, +1f, +2f, // 200-1.6kHz: Clear midrange
                +2f, +1.5f, +1f, +1f, +2f, +1.5f, +1f, 0f, 0f, -1f  // 2-16kHz: Controlled highs
            },
                TargetLoudness = -17f,  // More headroom (was -16)
                DynamicRange = 19f,     // More dynamic
                Compression = new CompressionSettings
                {
                    Threshold = -16f,   // Higher (was -18)
                    Ratio = 2.0f,       // Lower (was 2.5)
                    AttackTime = 15f,   // Slower (was 10)
                    ReleaseTime = 80f,  // Faster release
                    MakeupGain = 1.5f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -16f,
                    AttackTime = 0.1f,
                    ReleaseTime = 0.5f,
                    MaxGain = 6f
                }
            },

            [PlaybackSystem.ClubPA] = new PlaybackPreset
            {
                Name = "Club/DJ Sound System",
                Description = "Dance music optimized with enhanced bass and presence",
                FrequencyResponse = new float[]
            {
                // Club: Punchy but not muddy
                +2f, +3f, +3f, +2f, +1.5f, +1f, +0.5f, 0f, 0f, 0f,  // 20-160Hz: Reduced bass boost
                0f, 0f, +0.5f, +0.5f, +0.5f, +1f, +1.5f, +1.5f, +2f, +2f, // 200-1.6kHz: Clear mids
                +1.5f, +1f, +1.5f, +2f, +2f, +1f, +1f, +0.5f, 0f, -1f // 2-16kHz: Airy top
            },
                TargetLoudness = -12.5f, // Quieter (was -11)
                DynamicRange = 10f,      // More dynamic (was 8)
                Compression = new CompressionSettings
                {
                    Threshold = -14f,    // Higher (was -15)
                    Ratio = 3.0f,        // Much lower (was 4)
                    AttackTime = 5f,     // Slower (was 3)
                    ReleaseTime = 40f,
                    MakeupGain = 2.0f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -12f,
                    AttackTime = 0.05f,
                    ReleaseTime = 0.2f,
                    MaxGain = 4f
                }
            },

            [PlaybackSystem.HiFiSpeakers] = new PlaybackPreset
            {
                Name = "Hi-Fi Home Speakers",
                Description = "Neutral response for critical listening in treated rooms",
                FrequencyResponse = new float[]
            {
                // Hi-Fi: Smooth air, tight bass
                -0.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,         // 20-160Hz: Tighter sub
                0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,             // 200-1.6kHz: Neutral
                0f, 0f, 0f, 0f, +0.5f, +1f, +1.5f, +1.5f, +1f, +0.5f // 2-16kHz: More air (10k+)
            },
                TargetLoudness = -19f,  // More headroom
                DynamicRange = 22f,     // Increased dynamic range
                Compression = new CompressionSettings
                {
                    Threshold = -22f,   // Higher threshold (was -25)
                    Ratio = 1.3f,       // Lower ratio (was 1.5)
                    AttackTime = 30f,   // Slower attack (was 20)
                    ReleaseTime = 200f,
                    MakeupGain = 0.5f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -19f, // Matched
                    AttackTime = 0.3f,
                    ReleaseTime = 2f,
                    MaxGain = 3f
                }
            },

            [PlaybackSystem.StudioMonitors] = new PlaybackPreset
            {
                Name = "Studio Near-Field Monitors",
                Description = "Reference standard for professional mixing",
                FrequencyResponse = new float[]
            {
                // Studio: Open and flat
                -1f, -0.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,       // 20-160Hz: Controlled low-lows
                0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,           // 200-1.6kHz: Flat
                0f, 0f, 0f, 0f, +0.5f, +0.5f, +0.5f, +1f, +1f, +0.5f // 2-16kHz: Subtle air
            },
                TargetLoudness = -21f,  // More dynamic (was -20)
                DynamicRange = 24f,     // More dynamic (was 22)
                Compression = new CompressionSettings
                {
                    Threshold = -26f,   // Higher (was -30)
                    Ratio = 1.15f,      // Lower (was 1.2)
                    AttackTime = 60f,   // Slower (was 50)
                    ReleaseTime = 250f, // Faster release for transparency
                    MakeupGain = 0f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -20f,
                    AttackTime = 0.5f,
                    ReleaseTime = 3f,
                    MaxGain = 2f
                }
            },

            [PlaybackSystem.Headphones] = new PlaybackPreset
            {
                Name = "Over-Ear Headphones",
                Description = "Compensated for typical headphone frequency response",
                FrequencyResponse = new float[]
            {
                // Headphone compensation curve
                +1f, +1f, +1f, +2f, +2f, +1f, +1f, 0f, 0f, -1f,    // 20-160Hz: Sub-bass boost
                -1f, -1f, 0f, +1f, +2f, +2f, +1f, 0f, -1f, -2f,     // 200-1.6kHz: Presence dip
                -1f, +1f, +2f, +1f, 0f, +1f, +2f, +3f, +2f, +1f     // 2-16kHz: Headphone curve
            },
                TargetLoudness = -16f,  // Personal listening level
                DynamicRange = 16f,
                Compression = new CompressionSettings
                {
                    Threshold = -20f,
                    Ratio = 2f,
                    AttackTime = 5f,
                    ReleaseTime = 80f,
                    MakeupGain = 2f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -16f,
                    AttackTime = 0.2f,
                    ReleaseTime = 1f,
                    MaxGain = 4f
                }
            },

            [PlaybackSystem.Earbuds] = new PlaybackPreset
            {
                Name = "In-Ear Monitors/Earbuds",
                Description = "Enhanced for in-ear acoustics and isolation",
                FrequencyResponse = new float[]
            {
                // IEM curve with bass compensation
                +2f, +3f, +3f, +2f, +1f, 0f, 0f, 0f, 0f, 0f,       // 20-160Hz: Bass boost for seal
                +1f, +2f, +2f, +2f, +2f, +1f, 0f, +1f, +2f, +3f,    // 200-1.6kHz: Clear vocals
                +3f, +2f, +1f, +2f, +3f, +2f, +1f, 0f, -1f, -2f     // 2-16kHz: Controlled highs
            },
                TargetLoudness = -14f,  // Mobile listening level
                DynamicRange = 12f,
                Compression = new CompressionSettings
                {
                    Threshold = -18f,
                    Ratio = 3f,
                    AttackTime = 2f,
                    ReleaseTime = 40f,
                    MakeupGain = 3f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -14f,
                    AttackTime = 0.1f,
                    ReleaseTime = 0.5f,
                    MaxGain = 5f
                }
            },

            [PlaybackSystem.CarStereo] = new PlaybackPreset
            {
                Name = "Car Stereo System",
                Description = "Optimized for road noise and cabin acoustics",
                FrequencyResponse = new float[]
            {
                // Car audio curve: Road noise compensation
                +2f, +2f, +1f, +1f, 0f, 0f, 0f, +1f, +2f, +3f,      // 20-160Hz: Engine compensation
                +3f, +2f, +1f, +2f, +3f, +4f, +4f, +3f, +2f, +1f,    // 200-1.6kHz: Vocal clarity
                +2f, +3f, +4f, +3f, +2f, +1f, +2f, +3f, +2f, +1f     // 2-16kHz: Wind noise comp
            },
                TargetLoudness = -12f,  // Loud environment
                DynamicRange = 10f,
                Compression = new CompressionSettings
                {
                    Threshold = -16f,
                    Ratio = 3.5f,
                    AttackTime = 3f,
                    ReleaseTime = 60f,
                    MakeupGain = 4f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -12f,
                    AttackTime = 0.05f,
                    ReleaseTime = 0.3f,
                    MaxGain = 6f
                }
            },

            [PlaybackSystem.Television] = new PlaybackPreset
            {
                Name = "Television/Soundbar",
                Description = "Dialogue clarity and late-night listening friendly",
                FrequencyResponse = new float[]
            {
                // TV curve: Dialogue focused
                -1f, -1f, 0f, +1f, +1f, +1f, +2f, +3f, +3f, +2f,    // 20-160Hz: Controlled bass
                +2f, +3f, +4f, +4f, +3f, +2f, +2f, +3f, +2f, +1f,    // 200-1.6kHz: Speech clarity
                +1f, +1f, +2f, +1f, 0f, 0f, +1f, +1f, 0f, -1f       // 2-16kHz: Soft highs
            },
                TargetLoudness = -15f,  // Living room level
                DynamicRange = 12f,
                Compression = new CompressionSettings
                {
                    Threshold = -18f,
                    Ratio = 4f,
                    AttackTime = 1f,
                    ReleaseTime = 30f,
                    MakeupGain = 3f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -15f,
                    AttackTime = 0.1f,
                    ReleaseTime = 0.8f,
                    MaxGain = 4f
                }
            },

            [PlaybackSystem.RadioBroadcast] = new PlaybackPreset
            {
                Name = "Radio Broadcast",
                Description = "FM/AM radio transmission standards",
                FrequencyResponse = new float[]
            {
                // Radio curve: Limited bandwidth, high compression
                0f, +1f, +2f, +2f, +2f, +2f, +2f, +2f, +2f, +1f,     // 20-160Hz: Controlled lows
                +2f, +3f, +4f, +4f, +4f, +3f, +3f, +4f, +3f, +2f,     // 200-1.6kHz: Forward mids
                +2f, +2f, +1f, +1f, 0f, 0f, +1f, 0f, -2f, -4f        // 2-16kHz: HF rolloff
            },
                TargetLoudness = -9f,   // Broadcast loudness
                DynamicRange = 6f,
                Compression = new CompressionSettings
                {
                    Threshold = -12f,
                    Ratio = 6f,
                    AttackTime = 0.5f,
                    ReleaseTime = 20f,
                    MakeupGain = 6f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -9f,
                    AttackTime = 0.02f,
                    ReleaseTime = 0.1f,
                    MaxGain = 8f
                }
            },

            [PlaybackSystem.Smartphone] = new PlaybackPreset
            {
                Name = "Smartphone/Tablet Speaker",
                Description = "Small speaker compensation with midrange focus",
                FrequencyResponse = new float[]
            {
                // Phone speaker curve: Limited bass, enhanced mids
                -6f, -4f, -2f, -1f, 0f, +1f, +2f, +3f, +4f, +4f,     // 20-160Hz: Bass limitation
                +5f, +6f, +6f, +5f, +4f, +4f, +5f, +6f, +5f, +4f,     // 200-1.6kHz: Strong mids
                +3f, +3f, +2f, +2f, +1f, +1f, +2f, +1f, 0f, -2f       // 2-16kHz: Controlled highs
            },
                TargetLoudness = -10f,  // Mobile environment
                DynamicRange = 8f,
                Compression = new CompressionSettings
                {
                    Threshold = -14f,
                    Ratio = 5f,
                    AttackTime = 1f,
                    ReleaseTime = 25f,
                    MakeupGain = 5f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -10f,
                    AttackTime = 0.02f,
                    ReleaseTime = 0.2f,
                    MaxGain = 8f
                }
            }
        };
    }

    /// <summary>
    /// Audio playback system presets based on audio engineering standards
    /// </summary>
    public enum PlaybackSystem
    {
        /// <summary>
        /// Concert sound reinforcement system for large venues
        /// </summary>
        ConcertPA,

        /// <summary>
        /// Nightclub or DJ sound system optimized for dance music
        /// </summary>
        ClubPA,

        /// <summary>
        /// High-fidelity home speakers for critical listening
        /// </summary>
        HiFiSpeakers,

        /// <summary>
        /// Near-field studio monitors for professional mixing
        /// </summary>
        StudioMonitors,

        /// <summary>
        /// Over-ear headphones for personal listening
        /// </summary>
        Headphones,

        /// <summary>
        /// In-ear monitors or earbuds for portable listening
        /// </summary>
        Earbuds,

        /// <summary>
        /// Automotive audio system compensated for road noise
        /// </summary>
        CarStereo,

        /// <summary>
        /// Television or soundbar speakers optimized for dialogue
        /// </summary>
        Television,

        /// <summary>
        /// Radio transmission standards for FM/AM broadcast
        /// </summary>
        RadioBroadcast,

        /// <summary>
        /// Smartphone or tablet built-in speakers
        /// </summary>
        Smartphone
    }

    /// <summary>
    /// Preset configuration for specific playback systems
    /// </summary>
    public class PlaybackPreset
    {
        /// <summary>
        /// Human-readable name of the preset
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Detailed description of the preset's intended use
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 30-band EQ frequency response curve in dB (20Hz to 16kHz)
        /// </summary>
        public float[] FrequencyResponse { get; set; } = new float[30];

        /// <summary>
        /// Target loudness level in LUFS for optimal playback
        /// </summary>
        public float TargetLoudness { get; set; }

        /// <summary>
        /// Recommended dynamic range in dB for the playback system
        /// </summary>
        public float DynamicRange { get; set; }

        /// <summary>
        /// Compression settings optimized for the playback system
        /// </summary>
        public CompressionSettings Compression { get; set; } = new();

        /// <summary>
        /// Dynamic amplifier settings for automatic level control
        /// </summary>
        public DynamicAmpSettings DynamicAmp { get; set; } = new();
    }
}
