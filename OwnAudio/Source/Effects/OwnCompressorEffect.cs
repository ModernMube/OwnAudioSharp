using System;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Ready made compressor setups. Each one sets every parameter, so a preset never
    /// inherits anything from the previous state.
    /// </summary>
    public enum OwnCompressorPreset
    {
        /// <summary>
        /// Clean, transparent all-rounder: 4:1, medium knee, peak detection.
        /// </summary>
        Default,

        /// <summary>
        /// Lead vocal: soft knee, RMS detection, auto release, gentle high-pass on the key.
        /// </summary>
        Vocal,

        /// <summary>
        /// Mix bus glue: 2:1, slow attack, auto release, fully linked.
        /// </summary>
        Bus,

        /// <summary>
        /// Drum bus punch: medium attack lets the transient through, fast release.
        /// </summary>
        Drums,

        /// <summary>
        /// Bass: even level, the key high-pass keeps the sub from pumping the rest.
        /// </summary>
        Bass,

        /// <summary>
        /// Mastering: low ratio, wide knee, 2 ms look-ahead, mid/side.
        /// </summary>
        Mastering,

        /// <summary>
        /// Parallel (New York) compression: heavy squash blended under the dry.
        /// </summary>
        Parallel,

        /// <summary>
        /// Classic feedback topology, smooth and program dependent.
        /// </summary>
        Classic,

        /// <summary>
        /// Speech: RMS detection, consistent level, auto makeup.
        /// </summary>
        Podcast
    }

    /// <summary>
    /// Level detector of the compressor.
    /// </summary>
    public enum OwnCompressorDetector
    {
        /// <summary>
        /// Instantaneous peak, catches every transient.
        /// </summary>
        Peak = 0,

        /// <summary>
        /// 10 ms RMS, follows perceived loudness.
        /// </summary>
        Rms = 1
    }

    /// <summary>
    /// Where the detector listens.
    /// </summary>
    public enum OwnCompressorTopology
    {
        /// <summary>
        /// Detector on the input: precise, the ratio means what it says.
        /// </summary>
        FeedForward = 0,

        /// <summary>
        /// Detector on the compressed output: softer, vintage style. Look-ahead only
        /// delays the audio in this mode.
        /// </summary>
        Feedback = 1
    }

    /// <summary>
    /// Channel pair the compressor works on.
    /// </summary>
    public enum OwnCompressorChannelMode
    {
        /// <summary>
        /// Left and right.
        /// </summary>
        LeftRight = 0,

        /// <summary>
        /// Mid and side; with StereoLink below 1 the side is compressed on its own.
        /// </summary>
        MidSide = 1
    }

    /// <summary>
    /// Log-domain compressor: soft knee, look-ahead, auto release, stereo link, mid/side and
    /// parallel mix. The DSP lives in the Rust engine - this is just the parameter model.
    /// </summary>
    public sealed class OwnCompressorEffect : NativeBackedEffect, IEffectProcessor
    {
        private const float DefaultSampleRate = 48000f;

        private float _mix = 1.0f;
        private float _threshold = -18.0f;
        private float _ratio = 4.0f;
        private float _knee = 6.0f;
        private float _attack = 10.0f;
        private float _release = 100.0f;
        private bool _autoRelease;
        private float _lookahead;
        private OwnCompressorDetector _detector = OwnCompressorDetector.Peak;
        private OwnCompressorTopology _topology = OwnCompressorTopology.FeedForward;
        private float _stereoLink = 1.0f;
        private OwnCompressorChannelMode _channelMode = OwnCompressorChannelMode.LeftRight;
        private float _sidechainHighPass;
        private float _makeup;
        private bool _autoMakeup;
        private float _range = 60.0f;

        /// <summary>
        /// Builds the compressor on its clean default setup.
        /// </summary>
        public OwnCompressorEffect()
            : base("OwnCompressor")
        {
        }

        /// <summary>
        /// Builds the compressor straight from a preset.
        /// </summary>
        /// <param name="preset">The setup to load.</param>
        public OwnCompressorEffect(OwnCompressorPreset preset) : this()
        {
            SetPreset(preset);
        }

        #region Properties

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "OwnCompressor"; }

        /// <summary>
        /// Parallel mix, 0.0 (dry) - 1.0 (fully compressed). The dry path is delayed by
        /// the look-ahead, so blending never comb filters.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Threshold in dBFS, -60 - 0.
        /// </summary>
        public float Threshold
        {
            get => _threshold;
            set => _threshold = FastClamp(value, -60f, 0f);
        }

        /// <summary>
        /// Ratio, 1 - 100. Anything above 20 behaves like a limiter.
        /// </summary>
        public float Ratio
        {
            get => _ratio;
            set => _ratio = FastClamp(value, 1f, 100f);
        }

        /// <summary>
        /// Knee width in dB, 0 (hard) - 24.
        /// </summary>
        public float Knee
        {
            get => _knee;
            set => _knee = FastClamp(value, 0f, 24f);
        }

        /// <summary>
        /// Attack in ms (10 % to 90 % of the step), 0.01 - 300.
        /// </summary>
        public float Attack
        {
            get => _attack;
            set => _attack = FastClamp(value, 0.01f, 300f);
        }

        /// <summary>
        /// Release in ms (10 % to 90 % of the step), 5 - 5000. With AutoRelease this is
        /// the fastest release; sustained compression stretches it up to four times.
        /// </summary>
        public float Release
        {
            get => _release;
            set => _release = FastClamp(value, 5f, 5000f);
        }

        /// <summary>
        /// Programme dependent release: fast after transients, slower after long,
        /// deep compression, which keeps bass from distorting and mixes from pumping.
        /// </summary>
        public bool AutoRelease
        {
            get => _autoRelease;
            set => _autoRelease = value;
        }

        /// <summary>
        /// Look-ahead in ms, 0 - 10. Adds the same latency, the mixer re-aligns the tracks.
        /// Moving it while playing jumps the audio, so better set it up front.
        /// </summary>
        public float Lookahead
        {
            get => _lookahead;
            set => _lookahead = FastClamp(value, 0f, 10f);
        }

        /// <summary>
        /// Peak or RMS detection.
        /// </summary>
        public OwnCompressorDetector Detector
        {
            get => _detector;
            set => _detector = value;
        }

        /// <summary>
        /// Feed-forward or feedback detection.
        /// </summary>
        public OwnCompressorTopology Topology
        {
            get => _topology;
            set => _topology = value;
        }

        /// <summary>
        /// Stereo link, 0.0 (independent channels) - 1.0 (both follow the louder one).
        /// </summary>
        public float StereoLink
        {
            get => _stereoLink;
            set => _stereoLink = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Left/right or mid/side processing.
        /// </summary>
        public OwnCompressorChannelMode ChannelMode
        {
            get => _channelMode;
            set => _channelMode = value;
        }

        /// <summary>
        /// Sidechain high-pass in Hz, 0 (off) or 20 - 500. Keeps low end from driving
        /// the gain reduction; the audio path itself is not filtered.
        /// </summary>
        public float SidechainHighPass
        {
            get => _sidechainHighPass;
            set => _sidechainHighPass = value < 20f ? 0f : Math.Min(value, 500f);
        }

        /// <summary>
        /// Makeup gain in dB, -24 - +24. Added on top of AutoMakeup.
        /// </summary>
        public float Makeup
        {
            get => _makeup;
            set => _makeup = FastClamp(value, -24f, 24f);
        }

        /// <summary>
        /// Automatic makeup, half of the gain reduction a 0 dBFS signal would get.
        /// </summary>
        public bool AutoMakeup
        {
            get => _autoMakeup;
            set => _autoMakeup = value;
        }

        /// <summary>
        /// Range: the most gain reduction allowed, in dB, 0 - 60.
        /// </summary>
        public float Range
        {
            get => _range;
            set => _range = FastClamp(value, 0f, 60f);
        }

        /// <summary>
        /// Latency in frames for the mixer's delay compensation.
        /// </summary>
        public int LatencySamples => _native.IsReady
            ? _native.LatencySamples
            : (int)MathF.Round(_lookahead * (_config?.SampleRate ?? DefaultSampleRate) / 1000f);

        /// <summary>
        /// Deepest gain reduction of the last processed block in dB (0 or negative).
        /// Only moves while this instance is processed directly, not as a mixer twin.
        /// </summary>
        public float GainReductionDb
        {
            get
            {
                float _gain = _native.GetParam(NativeEffectEngine.MeterCurrentGain) ?? 1f;
                return _gain > 0f ? 20f * MathF.Log10(_gain) : -120f;
            }
        }

        /// <summary>
        /// Detected input peak of the last processed block, linear.
        /// </summary>
        public float InputLevel => _native.GetParam(NativeEffectEngine.MeterInputLevel) ?? 0f;

        #endregion

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset">The setup to load.</param>
        public void SetPreset(OwnCompressorPreset preset)
        {
            // Wipe everything back to neutral first, so a preset never drags along
            // some leftover sidechain filter or look-ahead from the last one.
            Mix = 1f; Knee = 6f; AutoRelease = false; Lookahead = 0f; Detector = OwnCompressorDetector.Peak;
            Topology = OwnCompressorTopology.FeedForward; StereoLink = 1f;
            ChannelMode = OwnCompressorChannelMode.LeftRight; SidechainHighPass = 0f;
            Makeup = 0f; AutoMakeup = false; Range = 60f;

            switch (preset)
            {
                case OwnCompressorPreset.Vocal:
                    Threshold = -20f; Ratio = 3f; Knee = 10f; Attack = 5f; Release = 80f;
                    AutoRelease = true; Detector = OwnCompressorDetector.Rms; SidechainHighPass = 100f;
                    AutoMakeup = true; break;
                case OwnCompressorPreset.Bus:
                    Threshold = -14f; Ratio = 2f; Knee = 8f; Attack = 30f; Release = 200f;
                    AutoRelease = true; Makeup = 1.5f; Range = 6f; break;
                case OwnCompressorPreset.Drums:
                    Threshold = -16f; Ratio = 4f; Knee = 4f; Attack = 15f; Release = 60f;
                    SidechainHighPass = 60f; Makeup = 3f; break;
                case OwnCompressorPreset.Bass:
                    Threshold = -18f; Ratio = 4f; Knee = 6f; Attack = 20f; Release = 150f;
                    AutoRelease = true; Detector = OwnCompressorDetector.Rms; Makeup = 3f; break;
                case OwnCompressorPreset.Mastering:
                    Threshold = -10f; Ratio = 1.5f; Knee = 12f; Attack = 30f; Release = 300f;
                    AutoRelease = true; Lookahead = 2f; ChannelMode = OwnCompressorChannelMode.MidSide;
                    StereoLink = 0.8f; SidechainHighPass = 40f; Range = 4f; break;
                case OwnCompressorPreset.Parallel:
                    Threshold = -30f; Ratio = 10f; Knee = 2f; Attack = 1f; Release = 80f;
                    Makeup = 10f; Mix = 0.35f; break;
                case OwnCompressorPreset.Classic:
                    Threshold = -20f; Ratio = 4f; Knee = 12f; Attack = 10f; Release = 150f;
                    AutoRelease = true; Topology = OwnCompressorTopology.Feedback; Makeup = 3f; break;
                case OwnCompressorPreset.Podcast:
                    Threshold = -24f; Ratio = 3f; Knee = 8f; Attack = 8f; Release = 200f;
                    Detector = OwnCompressorDetector.Rms; SidechainHighPass = 80f; AutoMakeup = true; break;
                default:
                    Threshold = -18f; Ratio = 4f; Knee = 6f; Attack = 10f; Release = 100f; break;
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"OwnCompressor: Thr={_threshold:F1}dB, Ratio={_ratio:F1}:1, Knee={_knee:F1}dB, " +
                   $"Att={_attack:F1}ms, Rel={_release:F0}ms, LA={_lookahead:F1}ms, Mix={_mix:F2}";
        }

        private static float FastClamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
