using OwnaudioNET.Effects;
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
                },
                Compressor = new OwnCompressorSettings
                {
                    ThresholdDb = -18f, Ratio = 1.8f, KneeDb = 8f,
                    AttackMs = 15f, ReleaseMs = 80f, AutoRelease = true, LookaheadMs = 2f,
                    Detector = OwnCompressorDetector.Peak,
                    ChannelMode = OwnCompressorChannelMode.MidSide, StereoLink = 0.8f,
                    SidechainHighPassHz = 60f, MakeupDb = 1.5f, RangeDb = 6f
                },
                Leveler = new OwnDynamicAmpSettings
                {
                    TargetLoudness = -16f, WindowSeconds = 12f,
                    RiseRateDbPerSec = 1f, FallRateDbPerSec = 2f, ToleranceDb = 1.5f, SmoothingMs = 600f,
                    RelativeGateLu = -10f,
                    CeilingDbtp = -1f, LookaheadMs = 5f, LimiterReleaseMs = 150f
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
                },
                Compressor = new OwnCompressorSettings
                {
                    ThresholdDb = -16f, Ratio = 2.5f, KneeDb = 4f,
                    AttackMs = 5f, ReleaseMs = 40f, AutoRelease = false, LookaheadMs = 1f,
                    Detector = OwnCompressorDetector.Peak,
                    ChannelMode = OwnCompressorChannelMode.LeftRight, StereoLink = 1f,
                    SidechainHighPassHz = 100f, MakeupDb = 2f, RangeDb = 8f
                },
                Leveler = new OwnDynamicAmpSettings
                {
                    TargetLoudness = -11f, WindowSeconds = 4f,
                    RiseRateDbPerSec = 3f, FallRateDbPerSec = 6f, ToleranceDb = 0.5f, SmoothingMs = 200f,
                    RelativeGateLu = -12f,
                    CeilingDbtp = -0.5f, LookaheadMs = 5f, LimiterReleaseMs = 80f
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
                },
                Compressor = new OwnCompressorSettings
                {
                    ThresholdDb = -24f, Ratio = 1.2f, KneeDb = 12f,
                    AttackMs = 30f, ReleaseMs = 200f, AutoRelease = true, LookaheadMs = 2f,
                    Detector = OwnCompressorDetector.Rms,
                    ChannelMode = OwnCompressorChannelMode.MidSide, StereoLink = 0.8f,
                    SidechainHighPassHz = 30f, MakeupDb = 0.5f, RangeDb = 3f
                },
                Leveler = new OwnDynamicAmpSettings
                {
                    TargetLoudness = -18f, WindowSeconds = 30f,
                    RiseRateDbPerSec = 0.5f, FallRateDbPerSec = 1f, ToleranceDb = 2f, SmoothingMs = 1500f,
                    RelativeGateLu = -8f,
                    CeilingDbtp = -1f, LookaheadMs = 5f, LimiterReleaseMs = 250f
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
                },
                Compressor = new OwnCompressorSettings
                {
                    ThresholdDb = -28f, Ratio = 1.1f, KneeDb = 12f,
                    AttackMs = 60f, ReleaseMs = 250f, AutoRelease = true, LookaheadMs = 2f,
                    Detector = OwnCompressorDetector.Rms,
                    ChannelMode = OwnCompressorChannelMode.MidSide, StereoLink = 0.9f,
                    SidechainHighPassHz = 20f, MakeupDb = 0f, RangeDb = 2f
                },
                Leveler = new OwnDynamicAmpSettings
                {
                    TargetLoudness = -20f, WindowSeconds = 30f,
                    RiseRateDbPerSec = 0.3f, FallRateDbPerSec = 0.6f, ToleranceDb = 2f, SmoothingMs = 2000f,
                    RelativeGateLu = -6f,
                    CeilingDbtp = -1f, LookaheadMs = 5f, LimiterReleaseMs = 300f
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
                },
                Compressor = new OwnCompressorSettings
                {
                    ThresholdDb = -22f, Ratio = 1.8f, KneeDb = 8f,
                    AttackMs = 5f, ReleaseMs = 80f, AutoRelease = true, LookaheadMs = 2f,
                    Detector = OwnCompressorDetector.Peak,
                    ChannelMode = OwnCompressorChannelMode.MidSide, StereoLink = 0.7f,
                    SidechainHighPassHz = 50f, MakeupDb = 2f, RangeDb = 6f
                },
                Leveler = new OwnDynamicAmpSettings
                {
                    TargetLoudness = -14f, WindowSeconds = 10f,
                    RiseRateDbPerSec = 1.5f, FallRateDbPerSec = 3f, ToleranceDb = 1f, SmoothingMs = 400f,
                    RelativeGateLu = -10f,
                    CeilingDbtp = -1f, LookaheadMs = 5f, LimiterReleaseMs = 150f
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
                },
                Compressor = new OwnCompressorSettings
                {
                    ThresholdDb = -20f, Ratio = 2.5f, KneeDb = 6f,
                    AttackMs = 2f, ReleaseMs = 40f, AutoRelease = true, LookaheadMs = 2f,
                    Detector = OwnCompressorDetector.Peak,
                    ChannelMode = OwnCompressorChannelMode.MidSide, StereoLink = 0.7f,
                    SidechainHighPassHz = 80f, MakeupDb = 3f, RangeDb = 8f
                },
                Leveler = new OwnDynamicAmpSettings
                {
                    TargetLoudness = -13f, WindowSeconds = 6f,
                    RiseRateDbPerSec = 2f, FallRateDbPerSec = 4f, ToleranceDb = 1f, SmoothingMs = 300f,
                    RelativeGateLu = -14f,
                    CeilingDbtp = -1f, LookaheadMs = 5f, LimiterReleaseMs = 120f
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
                },
                Compressor = new OwnCompressorSettings
                {
                    ThresholdDb = -18f, Ratio = 2.8f, KneeDb = 6f,
                    AttackMs = 3f, ReleaseMs = 60f, AutoRelease = true, LookaheadMs = 2f,
                    Detector = OwnCompressorDetector.Rms,
                    ChannelMode = OwnCompressorChannelMode.LeftRight, StereoLink = 1f,
                    SidechainHighPassHz = 60f, MakeupDb = 4f, RangeDb = 10f
                },
                Leveler = new OwnDynamicAmpSettings
                {
                    TargetLoudness = -11f, WindowSeconds = 3f,
                    RiseRateDbPerSec = 3f, FallRateDbPerSec = 6f, ToleranceDb = 0.5f, SmoothingMs = 250f,
                    RelativeGateLu = -20f,
                    CeilingDbtp = -1f, LookaheadMs = 5f, LimiterReleaseMs = 100f
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
                },
                Compressor = new OwnCompressorSettings
                {
                    ThresholdDb = -20f, Ratio = 3f, KneeDb = 6f,
                    AttackMs = 1f, ReleaseMs = 30f, AutoRelease = true, LookaheadMs = 3f,
                    Detector = OwnCompressorDetector.Rms,
                    ChannelMode = OwnCompressorChannelMode.LeftRight, StereoLink = 1f,
                    SidechainHighPassHz = 120f, MakeupDb = 3f, RangeDb = 10f
                },
                Leveler = new OwnDynamicAmpSettings
                {
                    TargetLoudness = -14f, WindowSeconds = 3f,
                    RiseRateDbPerSec = 4f, FallRateDbPerSec = 8f, ToleranceDb = 0.5f, SmoothingMs = 200f,
                    RelativeGateLu = -20f,
                    CeilingDbtp = -2f, LookaheadMs = 5f, LimiterReleaseMs = 100f
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
                },
                Compressor = new OwnCompressorSettings
                {
                    ThresholdDb = -14f, Ratio = 4.5f, KneeDb = 3f,
                    AttackMs = 0.5f, ReleaseMs = 20f, AutoRelease = true, LookaheadMs = 5f,
                    Detector = OwnCompressorDetector.Peak,
                    ChannelMode = OwnCompressorChannelMode.LeftRight, StereoLink = 1f,
                    SidechainHighPassHz = 80f, MakeupDb = 6f, RangeDb = 14f
                },
                Leveler = new OwnDynamicAmpSettings
                {
                    TargetLoudness = -10f, WindowSeconds = 2f,
                    RiseRateDbPerSec = 6f, FallRateDbPerSec = 12f, ToleranceDb = 0.3f, SmoothingMs = 100f,
                    RelativeGateLu = -24f,
                    CeilingDbtp = -1f, LookaheadMs = 5f, LimiterReleaseMs = 50f
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
                },
                Compressor = new OwnCompressorSettings
                {
                    ThresholdDb = -16f, Ratio = 3.5f, KneeDb = 4f,
                    AttackMs = 1f, ReleaseMs = 25f, AutoRelease = true, LookaheadMs = 3f,
                    Detector = OwnCompressorDetector.Peak,
                    ChannelMode = OwnCompressorChannelMode.MidSide, StereoLink = 0.6f,
                    SidechainHighPassHz = 150f, MakeupDb = 5f, RangeDb = 12f
                },
                Leveler = new OwnDynamicAmpSettings
                {
                    TargetLoudness = -11f, WindowSeconds = 3f,
                    RiseRateDbPerSec = 4f, FallRateDbPerSec = 8f, ToleranceDb = 0.5f, SmoothingMs = 150f,
                    RelativeGateLu = -20f,
                    CeilingDbtp = -1f, LookaheadMs = 5f, LimiterReleaseMs = 60f
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
        /// Compressor settings for this system, the older five-field form of <see cref="Compressor"/>.
        /// </summary>
        public CompressionSettings Compression { get; set; } = new CompressionSettings();

        /// <summary>
        /// AGC settings for this system, the older <see cref="DynamicAmpEffect"/> form.
        /// </summary>
        public DynamicAmpSettings DynamicAmp { get; set; } = new DynamicAmpSettings();

        /// <summary>
        /// The system's OwnCompressor. The whole setup compresses the base sample when the preset
        /// is baked; on a match its knee, timing, look-ahead, detector and key high-pass replace
        /// the measured ones, while threshold, ratio, range and the stereo handling stay measured.
        /// </summary>
        public OwnCompressorSettings Compressor { get; set; } = new OwnCompressorSettings();

        /// <summary>
        /// The system's OwnDynamicAmp. On a match its target, window, rates, tolerance, gates,
        /// true-peak ceiling and limiter timing replace the measured ones; the start gain, the
        /// boost and cut room and the freeze threshold stay measured. The ceiling also caps the
        /// baked base sample.
        /// </summary>
        public OwnDynamicAmpSettings Leveler { get; set; } = new OwnDynamicAmpSettings();
    }
}
