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
        private static readonly Dictionary<PlaybackSystem, PlaybackPreset> SystemPresets = new Dictionary<PlaybackSystem, PlaybackPreset>
        {
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
                TargetLoudness = -16f,  // OPTIMIZED: Concert standard
                DynamicRange = 19f,
                Compression = new CompressionSettings
                {
                    Threshold = -18f,   // OPTIMIZED: Lower threshold
                    Ratio = 1.8f,       // OPTIMIZED: More transparent (was 2.0)
                    AttackTime = 15f,
                    ReleaseTime = 80f,
                    MakeupGain = 1.5f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -16f,
                    AttackTime = 0.2f,  // OPTIMIZED: Faster, musical (was 0.1f)
                    ReleaseTime = 0.8f, // OPTIMIZED: Consistent with main processing (was 0.5f)
                    MaxGain = 3f        // OPTIMIZED: Stricter limit (was 6f)
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
                TargetLoudness = -11f,  // OPTIMIZED: Club standard (was -12.5f)
                DynamicRange = 10f,
                Compression = new CompressionSettings
                {
                    Threshold = -16f,   // OPTIMIZED: Higher threshold (was -14f)
                    Ratio = 2.5f,       // OPTIMIZED: Less aggressive (was 3.0f)
                    AttackTime = 5f,
                    ReleaseTime = 40f,
                    MakeupGain = 2.0f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -11f,
                    AttackTime = 0.15f, // OPTIMIZED: Faster but musical (was 0.05f)
                    ReleaseTime = 0.6f, // OPTIMIZED: More breathing room (was 0.2f)
                    MaxGain = 2.5f      // OPTIMIZED: Stricter limit (was 4f)
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
                TargetLoudness = -18f,  // OPTIMIZED: Hi-Fi dynamics (was -19f)
                DynamicRange = 22f,
                Compression = new CompressionSettings
                {
                    Threshold = -24f,   // OPTIMIZED: Even higher threshold (was -22f)
                    Ratio = 1.2f,       // OPTIMIZED: Ultra-transparent (was 1.3f)
                    AttackTime = 30f,
                    ReleaseTime = 200f,
                    MakeupGain = 0.5f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -18f,
                    AttackTime = 0.4f,  // OPTIMIZED: Slower for Hi-Fi (was 0.3f)
                    ReleaseTime = 1.5f, // OPTIMIZED: Faster than before (was 2f)
                    MaxGain = 2f        // OPTIMIZED: Minimal intervention (was 3f)
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
                TargetLoudness = -20f,  // OPTIMIZED: Studio reference (was -21f)
                DynamicRange = 24f,
                Compression = new CompressionSettings
                {
                    Threshold = -28f,   // OPTIMIZED: Minimal intervention (was -26f)
                    Ratio = 1.1f,       // OPTIMIZED: Barely noticeable (was 1.15f)
                    AttackTime = 60f,
                    ReleaseTime = 250f,
                    MakeupGain = 0f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -20f,
                    AttackTime = 0.5f,  // OPTIMIZED: Unchanged, already optimal
                    ReleaseTime = 2f,   // OPTIMIZED: Slightly faster (was 3f)
                    MaxGain = 1.5f      // OPTIMIZED: Absolute minimum (was 2f)
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
                TargetLoudness = -14f,  // OPTIMIZED: Personal listening (was -16f)
                DynamicRange = 16f,
                Compression = new CompressionSettings
                {
                    Threshold = -22f,   // OPTIMIZED: Higher threshold (was -20f)
                    Ratio = 1.8f,       // OPTIMIZED: Gentler (was 2f)
                    AttackTime = 5f,
                    ReleaseTime = 80f,
                    MakeupGain = 2f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -14f,
                    AttackTime = 0.25f, // OPTIMIZED: Slightly slower (was 0.2f)
                    ReleaseTime = 1f,   // OPTIMIZED: Unchanged, good balance
                    MaxGain = 2.5f      // OPTIMIZED: Reduced (was 4f)
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
                TargetLoudness = -13f,  // OPTIMIZED: Mobile environment (was -14f)
                DynamicRange = 12f,
                Compression = new CompressionSettings
                {
                    Threshold = -20f,   // OPTIMIZED: Higher threshold (was -18f)
                    Ratio = 2.5f,       // OPTIMIZED: Less aggressive (was 3f)
                    AttackTime = 2f,
                    ReleaseTime = 40f,
                    MakeupGain = 3f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -13f,
                    AttackTime = 0.2f,  // OPTIMIZED: Faster but musical (was 0.1f)
                    ReleaseTime = 0.7f, // OPTIMIZED: More breathing (was 0.5f)
                    MaxGain = 3f        // OPTIMIZED: Reduced (was 5f)
                }
            },

            [PlaybackSystem.CarStereo] = new PlaybackPreset
            {
                Name = "Car Stereo System",
                Description = "Optimized for road noise and cabin acoustics",
                FrequencyResponse = new float[]
            {
                // OPTIMIZED: Car audio curve - moderated boosts
                +1.5f, +1.5f, +1f, +0.5f, 0f, 0f, 0f, +0.5f, +1f, +2f,      // 20-160Hz: Reduced bass boost
                +2.5f, +2f, +1.5f, +2f, +2.5f, +3f, +3f, +2.5f, +2f, +1.5f,  // 200-1.6kHz: Max +3dB (was +4dB)
                +2f, +2.5f, +3f, +2.5f, +2f, +1.5f, +2f, +2.5f, +2f, +1.5f   // 2-16kHz: Controlled highs
            },
                TargetLoudness = -11f,  // OPTIMIZED: Noisy environment (was -12f)
                DynamicRange = 10f,
                Compression = new CompressionSettings
                {
                    Threshold = -18f,   // OPTIMIZED: Higher threshold (was -16f)
                    Ratio = 2.8f,       // OPTIMIZED: Less extreme (was 3.5f)
                    AttackTime = 3f,
                    ReleaseTime = 60f,
                    MakeupGain = 4f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -11f,
                    AttackTime = 0.15f, // OPTIMIZED: Faster but musical (was 0.05f)
                    ReleaseTime = 0.5f, // OPTIMIZED: More breathing (was 0.3f)
                    MaxGain = 3f        // OPTIMIZED: Halved (was 6f)
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
                TargetLoudness = -14f,  // OPTIMIZED: Living room (was -15f)
                DynamicRange = 12f,
                Compression = new CompressionSettings
                {
                    Threshold = -20f,   // OPTIMIZED: Higher threshold (was -18f)
                    Ratio = 3.0f,       // OPTIMIZED: Less aggressive (was 4f)
                    AttackTime = 1f,
                    ReleaseTime = 30f,
                    MakeupGain = 3f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -14f,
                    AttackTime = 0.2f,  // OPTIMIZED: Consistent (was 0.1f)
                    ReleaseTime = 0.9f, // OPTIMIZED: Slightly slower (was 0.8f)
                    MaxGain = 2.5f      // OPTIMIZED: Reduced (was 4f)
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
                TargetLoudness = -10f,  // OPTIMIZED: Broadcast standard (was -9f)
                DynamicRange = 6f,
                Compression = new CompressionSettings
                {
                    Threshold = -14f,   // OPTIMIZED: Higher threshold (was -12f)
                    Ratio = 4.5f,       // OPTIMIZED: Less extreme (was 6f)
                    AttackTime = 0.5f,
                    ReleaseTime = 20f,
                    MakeupGain = 6f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -10f,
                    AttackTime = 0.1f,  // OPTIMIZED: Slower for musicality (was 0.02f)
                    ReleaseTime = 0.3f, // OPTIMIZED: More breathing (was 0.1f)
                    MaxGain = 4f        // OPTIMIZED: Halved (was 8f)
                }
            },

            [PlaybackSystem.Smartphone] = new PlaybackPreset
            {
                Name = "Smartphone/Tablet Speaker",
                Description = "Small speaker compensation with midrange focus",
                FrequencyResponse = new float[]
            {
                // OPTIMIZED: Phone speaker curve - moderated boosts
                -4f, -3f, -2f, -1f, 0f, +1f, +1.5f, +2f, +2.5f, +3f,     // 20-160Hz: Less bass cut
                +3.5f, +4f, +4f, +3.5f, +3f, +3f, +3.5f, +4f, +3.5f, +3f, // 200-1.6kHz: Max +4dB (was +6dB)
                +2.5f, +2.5f, +2f, +2f, +1.5f, +1.5f, +2f, +1.5f, +1f, -1f // 2-16kHz: Gentler highs
            },
                TargetLoudness = -11f,  // OPTIMIZED: Mobile environment (was -10f)
                DynamicRange = 8f,
                Compression = new CompressionSettings
                {
                    Threshold = -16f,   // OPTIMIZED: Higher threshold (was -14f)
                    Ratio = 3.5f,       // OPTIMIZED: Much less aggressive (was 5f)
                    AttackTime = 1f,
                    ReleaseTime = 25f,
                    MakeupGain = 5f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -11f,
                    AttackTime = 0.1f,  // OPTIMIZED: Much slower for musicality (was 0.02f)
                    ReleaseTime = 0.4f, // OPTIMIZED: More breathing (was 0.2f)
                    MaxGain = 4f        // OPTIMIZED: Halved (was 8f)
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
