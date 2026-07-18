using System.Collections.Generic;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// The baked-in playback system presets.
    /// </summary>
    partial class AudioAnalyzer
    {
        /// <summary>
        /// Copy of the preset table, safe to poke at.
        /// </summary>
        public static Dictionary<PlaybackSystem, PlaybackPreset> GetAvailablePresets()
        {
            return new Dictionary<PlaybackSystem, PlaybackPreset>(_systemPresets);
        }

        /// <summary>
        /// Curves and dynamics per playback system. Each FrequencyResponse row lines up
        /// with the 30 ISO bands, three lines of ten.
        /// </summary>
        private static readonly Dictionary<PlaybackSystem, PlaybackPreset> _systemPresets = new Dictionary<PlaybackSystem, PlaybackPreset>
        {
            [PlaybackSystem.ConcertPA] = new PlaybackPreset
            {
                Name = "Concert PA System",
                Description = "Large venue sound reinforcement with extended dynamics",
                FrequencyResponse = new float[]
            {
                -3f, -2f, -1f, 0f, +1f, +1f, +0.5f, 0f, 0f, 0f,
                +0.5f, 0.5f, 0f, 0f, 0f, -0.5f, -0.5f, 0f, +1f, +2f,
                +2f, +1.5f, +1f, +1f, +2f, +1.5f, +1f, 0f, 0f, -1f
            },
                TargetLoudness = -16f,
                DynamicRange = 19f,
                Compression = new CompressionSettings
                {
                    Threshold = -18f,
                    Ratio = 1.8f,
                    AttackTime = 15f,
                    ReleaseTime = 80f,
                    MakeupGain = 1.5f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -16f,
                    AttackTime = 0.2f,
                    ReleaseTime = 0.8f,
                    MaxGain = 3f
                }
            },

            [PlaybackSystem.ClubPA] = new PlaybackPreset
            {
                Name = "Club/DJ Sound System",
                Description = "Dance music optimized with enhanced bass and presence",
                FrequencyResponse = new float[]
            {
                +2f, +3f, +3f, +2f, +1.5f, +1f, +0.5f, 0f, 0f, 0f,
                0f, 0f, +0.5f, +0.5f, +0.5f, +1f, +1.5f, +1.5f, +2f, +2f,
                +1.5f, +1f, +1.5f, +2f, +2f, +1f, +1f, +0.5f, 0f, -1f
            },
                TargetLoudness = -11f,
                DynamicRange = 10f,
                Compression = new CompressionSettings
                {
                    Threshold = -16f,
                    Ratio = 2.5f,
                    AttackTime = 5f,
                    ReleaseTime = 40f,
                    MakeupGain = 2.0f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -11f,
                    AttackTime = 0.15f,
                    ReleaseTime = 0.6f,
                    MaxGain = 2.5f
                }
            },

            [PlaybackSystem.HiFiSpeakers] = new PlaybackPreset
            {
                Name = "Hi-Fi Home Speakers",
                Description = "Neutral response for critical listening in treated rooms",
                FrequencyResponse = new float[]
            {
                -0.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
                0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
                0f, 0f, 0f, 0f, +0.5f, +1f, +1.5f, +1.5f, +1f, +0.5f
            },
                TargetLoudness = -18f,
                DynamicRange = 22f,
                Compression = new CompressionSettings
                {
                    Threshold = -24f,
                    Ratio = 1.2f,
                    AttackTime = 30f,
                    ReleaseTime = 200f,
                    MakeupGain = 0.5f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -18f,
                    AttackTime = 0.4f,
                    ReleaseTime = 1.5f,
                    MaxGain = 2f
                }
            },

            [PlaybackSystem.StudioMonitors] = new PlaybackPreset
            {
                Name = "Studio Near-Field Monitors",
                Description = "Reference standard for professional mixing",
                FrequencyResponse = new float[]
            {
                -1f, -0.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
                0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
                0f, 0f, 0f, 0f, +0.5f, +0.5f, +0.5f, +1f, +1f, +0.5f
            },
                TargetLoudness = -20f,
                DynamicRange = 24f,
                Compression = new CompressionSettings
                {
                    Threshold = -28f,
                    Ratio = 1.1f,
                    AttackTime = 60f,
                    ReleaseTime = 250f,
                    MakeupGain = 0f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -20f,
                    AttackTime = 0.5f,
                    ReleaseTime = 2f,
                    MaxGain = 1.5f
                }
            },

            [PlaybackSystem.Headphones] = new PlaybackPreset
            {
                Name = "Over-Ear Headphones",
                Description = "Compensated for typical headphone frequency response",
                FrequencyResponse = new float[]
            {
                +1f, +1f, +1f, +2f, +2f, +1f, +1f, 0f, 0f, -1f,
                -1f, -1f, 0f, +1f, +2f, +2f, +1f, 0f, -1f, -2f,
                -1f, +1f, +2f, +1f, 0f, +1f, +2f, +3f, +2f, +1f
            },
                TargetLoudness = -14f,
                DynamicRange = 16f,
                Compression = new CompressionSettings
                {
                    Threshold = -22f,
                    Ratio = 1.8f,
                    AttackTime = 5f,
                    ReleaseTime = 80f,
                    MakeupGain = 2f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -14f,
                    AttackTime = 0.25f,
                    ReleaseTime = 1f,
                    MaxGain = 2.5f
                }
            },

            [PlaybackSystem.Earbuds] = new PlaybackPreset
            {
                Name = "In-Ear Monitors/Earbuds",
                Description = "Enhanced for in-ear acoustics and isolation",
                FrequencyResponse = new float[]
            {
                +2f, +3f, +3f, +2f, +1f, 0f, 0f, 0f, 0f, 0f,
                +1f, +2f, +2f, +2f, +2f, +1f, 0f, +1f, +2f, +3f,
                +3f, +2f, +1f, +2f, +3f, +2f, +1f, 0f, -1f, -2f
            },
                TargetLoudness = -13f,
                DynamicRange = 12f,
                Compression = new CompressionSettings
                {
                    Threshold = -20f,
                    Ratio = 2.5f,
                    AttackTime = 2f,
                    ReleaseTime = 40f,
                    MakeupGain = 3f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -13f,
                    AttackTime = 0.2f,
                    ReleaseTime = 0.7f,
                    MaxGain = 3f
                }
            },

            [PlaybackSystem.CarStereo] = new PlaybackPreset
            {
                Name = "Car Stereo System",
                Description = "Optimized for road noise and cabin acoustics",
                FrequencyResponse = new float[]
            {
                +1.5f, +1.5f, +1f, +0.5f, 0f, 0f, 0f, +0.5f, +1f, +2f,
                +2.5f, +2f, +1.5f, +2f, +2.5f, +3f, +3f, +2.5f, +2f, +1.5f,
                +2f, +2.5f, +3f, +2.5f, +2f, +1.5f, +2f, +2.5f, +2f, +1.5f
            },
                TargetLoudness = -11f,
                DynamicRange = 10f,
                Compression = new CompressionSettings
                {
                    Threshold = -18f,
                    Ratio = 2.8f,
                    AttackTime = 3f,
                    ReleaseTime = 60f,
                    MakeupGain = 4f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -11f,
                    AttackTime = 0.15f,
                    ReleaseTime = 0.5f,
                    MaxGain = 3f
                }
            },

            [PlaybackSystem.Television] = new PlaybackPreset
            {
                Name = "Television/Soundbar",
                Description = "Dialogue clarity and late-night listening friendly",
                FrequencyResponse = new float[]
            {
                -1f, -1f, 0f, +1f, +1f, +1f, +2f, +3f, +3f, +2f,
                +2f, +3f, +4f, +4f, +3f, +2f, +2f, +3f, +2f, +1f,
                +1f, +1f, +2f, +1f, 0f, 0f, +1f, +1f, 0f, -1f
            },
                TargetLoudness = -14f,
                DynamicRange = 12f,
                Compression = new CompressionSettings
                {
                    Threshold = -20f,
                    Ratio = 3.0f,
                    AttackTime = 1f,
                    ReleaseTime = 30f,
                    MakeupGain = 3f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -14f,
                    AttackTime = 0.2f,
                    ReleaseTime = 0.9f,
                    MaxGain = 2.5f
                }
            },

            [PlaybackSystem.RadioBroadcast] = new PlaybackPreset
            {
                Name = "Radio Broadcast",
                Description = "FM/AM radio transmission standards",
                FrequencyResponse = new float[]
            {
                0f, +1f, +2f, +2f, +2f, +2f, +2f, +2f, +2f, +1f,
                +2f, +3f, +4f, +4f, +4f, +3f, +3f, +4f, +3f, +2f,
                +2f, +2f, +1f, +1f, 0f, 0f, +1f, 0f, -2f, -4f
            },
                TargetLoudness = -10f,
                DynamicRange = 6f,
                Compression = new CompressionSettings
                {
                    Threshold = -14f,
                    Ratio = 4.5f,
                    AttackTime = 0.5f,
                    ReleaseTime = 20f,
                    MakeupGain = 6f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -10f,
                    AttackTime = 0.1f,
                    ReleaseTime = 0.3f,
                    MaxGain = 4f
                }
            },

            [PlaybackSystem.Smartphone] = new PlaybackPreset
            {
                Name = "Smartphone/Tablet Speaker",
                Description = "Small speaker compensation with midrange focus",
                FrequencyResponse = new float[]
            {
                -4f, -3f, -2f, -1f, 0f, +1f, +1.5f, +2f, +2.5f, +3f,
                +3.5f, +4f, +4f, +3.5f, +3f, +3f, +3.5f, +4f, +3.5f, +3f,
                +2.5f, +2.5f, +2f, +2f, +1.5f, +1.5f, +2f, +1.5f, +1f, -1f
            },
                TargetLoudness = -11f,
                DynamicRange = 8f,
                Compression = new CompressionSettings
                {
                    Threshold = -16f,
                    Ratio = 3.5f,
                    AttackTime = 1f,
                    ReleaseTime = 25f,
                    MakeupGain = 5f
                },
                DynamicAmp = new DynamicAmpSettings
                {
                    TargetLevel = -11f,
                    AttackTime = 0.1f,
                    ReleaseTime = 0.4f,
                    MaxGain = 4f
                }
            }
        };
    }

    /// <summary>
    /// Playback systems we have a preset curve for.
    /// </summary>
    public enum PlaybackSystem
    {
        /// <summary>
        /// Large venue sound reinforcement.
        /// </summary>
        ConcertPA,

        /// <summary>
        /// Club / DJ rig, dance music.
        /// </summary>
        ClubPA,

        /// <summary>
        /// Hi-Fi home speakers.
        /// </summary>
        HiFiSpeakers,

        /// <summary>
        /// Near-field studio monitors.
        /// </summary>
        StudioMonitors,

        /// <summary>
        /// Over-ear headphones.
        /// </summary>
        Headphones,

        /// <summary>
        /// IEMs and earbuds.
        /// </summary>
        Earbuds,

        /// <summary>
        /// Car audio, compensated for road noise.
        /// </summary>
        CarStereo,

        /// <summary>
        /// TV or soundbar, dialogue first.
        /// </summary>
        Television,

        /// <summary>
        /// FM/AM broadcast chain.
        /// </summary>
        RadioBroadcast,

        /// <summary>
        /// Phone or tablet speaker.
        /// </summary>
        Smartphone
    }

    /// <summary>
    /// One playback system preset - curve plus its dynamics settings.
    /// </summary>
    public class PlaybackPreset
    {
        /// <summary>
        /// Display name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// What it's meant for.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 30 band EQ curve in dB, 20Hz to 16kHz.
        /// </summary>
        public float[] FrequencyResponse { get; set; } = new float[30];

        /// <summary>
        /// Target loudness in LUFS.
        /// </summary>
        public float TargetLoudness { get; set; }

        /// <summary>
        /// Dynamic range the system can take, dB.
        /// </summary>
        public float DynamicRange { get; set; }

        /// <summary>
        /// Compressor settings for this system.
        /// </summary>
        public CompressionSettings Compression { get; set; } = new CompressionSettings();

        /// <summary>
        /// AGC settings for this system.
        /// </summary>
        public DynamicAmpSettings DynamicAmp { get; set; } = new DynamicAmpSettings();
    }
}
