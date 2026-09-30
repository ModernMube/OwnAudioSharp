using System;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Ready made level rider setups, the same use cases as <see cref="DynamicAmpPreset"/>.
    /// Each one sets every parameter, so a preset never inherits anything from the previous state.
    /// </summary>
    public enum OwnDynamicAmpPreset
    {
        /// <summary>
        /// Transparent general purpose rider, -14 LUFS, 8 s memory, -1 dBTP ceiling.
        /// </summary>
        Default,

        /// <summary>
        /// Spoken word, -16 LUFS: quicker response, pauses never pump up the room noise.
        /// </summary>
        Speech,

        /// <summary>
        /// Music, -14 LUFS: long memory and a wide tolerance, keeps the arrangement's dynamics.
        /// </summary>
        Music,

        /// <summary>
        /// EBU R128 broadcast, -23 LUFS, tight tolerance.
        /// </summary>
        Broadcast,

        /// <summary>
        /// Very slow mastering glue, small correction range, no audible movement.
        /// </summary>
        Mastering,

        /// <summary>
        /// Live and stage use: fast response, short look-ahead.
        /// </summary>
        Live,

        /// <summary>
        /// Barely does anything, just catches the long term drift.
        /// </summary>
        Transparent
    }

    /// <summary>
    /// Loudness (LUFS) based level rider with a true-peak limiter, the grown-up twin of
    /// <see cref="DynamicAmpEffect"/>. The DSP lives in the Rust engine - this is just the parameter model.
    /// </summary>
    public sealed class OwnDynamicAmpEffect : NativeBackedEffect, IEffectProcessor
    {
        private const float DefaultSampleRate = 48000f;
        private const uint MeterProgramLoudness = 1002;
        private const uint MeterMomentaryLoudness = 1003;
        private const uint MeterLimiterGain = 1004;
        private const float SilentLufs = -120f;

        private float _mix = 1.0f;
        private float _targetLoudness = -14.0f;
        private float _window = 8.0f;
        private float _maxBoost = 12.0f;
        private float _maxCut = 12.0f;
        private float _riseRate = 1.5f;
        private float _fallRate = 3.0f;
        private float _tolerance = 1.0f;
        private float _smoothing = 300.0f;
        private float _relativeGate = -10.0f;
        private float _freezeThreshold = -55.0f;
        private float _ceiling = -1.0f;
        private bool _limiterEnabled = true;
        private float _lookahead = 5.0f;
        private float _limiterRelease = 150.0f;
        private float _initialGain;

        /// <summary>
        /// Builds the rider on its default setup.
        /// </summary>
        public OwnDynamicAmpEffect()
            : base("OwnDynamicAmp")
        {
        }

        /// <summary>
        /// Builds the rider straight from a preset.
        /// </summary>
        /// <param name="preset">The setup to load.</param>
        public OwnDynamicAmpEffect(OwnDynamicAmpPreset preset) : this()
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Builds the rider aiming at a given loudness, everything else on defaults.
        /// </summary>
        /// <param name="targetLoudness">Target programme loudness in LUFS, -40 - -5.</param>
        /// <param name="ceiling">True-peak ceiling in dBTP, -20 - 0.</param>
        public OwnDynamicAmpEffect(float targetLoudness, float ceiling = -1.0f) : this()
        {
            TargetLoudness = targetLoudness;
            Ceiling = ceiling;
        }

        #region Properties

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "OwnDynamicAmp"; }

        /// <summary>
        /// No dry path here, the value is kept for the interface but the rider always runs fully wet.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Target programme loudness in LUFS, -40 - -5. -14 streaming, -16 podcast, -23 EBU R128.
        /// </summary>
        public float TargetLoudness
        {
            get => _targetLoudness;
            set => _targetLoudness = FastClamp(value, -40f, -5f);
        }

        /// <summary>
        /// Memory of the loudness estimate in seconds, 0.4 - 60. Longer windows follow the
        /// song rather than the phrase and keep more of the dynamics.
        /// </summary>
        public float Window
        {
            get => _window;
            set => _window = FastClamp(value, 0.4f, 60f);
        }

        /// <summary>
        /// Most the rider may lift, in dB, 0 - 30.
        /// </summary>
        public float MaxBoost
        {
            get => _maxBoost;
            set => _maxBoost = FastClamp(value, 0f, 30f);
        }

        /// <summary>
        /// Most the rider may pull down, in dB, 0 - 30.
        /// </summary>
        public float MaxCut
        {
            get => _maxCut;
            set => _maxCut = FastClamp(value, 0f, 30f);
        }

        /// <summary>
        /// Fastest upward gain movement in dB per second, 0.1 - 20.
        /// </summary>
        public float RiseRate
        {
            get => _riseRate;
            set => _riseRate = FastClamp(value, 0.1f, 20f);
        }

        /// <summary>
        /// Fastest downward gain movement in dB per second, 0.1 - 40.
        /// </summary>
        public float FallRate
        {
            get => _fallRate;
            set => _fallRate = FastClamp(value, 0.1f, 40f);
        }

        /// <summary>
        /// Dead band around the target in dB, 0 - 6. Material closer to the target than this
        /// is left untouched.
        /// </summary>
        public float Tolerance
        {
            get => _tolerance;
            set => _tolerance = FastClamp(value, 0f, 6f);
        }

        /// <summary>
        /// Rounding of the gain curve in ms, 10 - 5000. Removes the corners of the rate
        /// limited movement so the rides stay inaudible.
        /// </summary>
        public float Smoothing
        {
            get => _smoothing;
            set => _smoothing = FastClamp(value, 10f, 5000f);
        }

        /// <summary>
        /// Relative gate in LU below the programme loudness, -40 - -1. Quieter passages than
        /// this do not move the gain, so a pianissimo stays a pianissimo.
        /// </summary>
        public float RelativeGate
        {
            get => _relativeGate;
            set => _relativeGate = FastClamp(value, -40f, -1f);
        }

        /// <summary>
        /// Absolute freeze threshold in LUFS, -90 - -30. Below it the gain holds, so silence
        /// and noise are never pumped up.
        /// </summary>
        public float FreezeThreshold
        {
            get => _freezeThreshold;
            set => _freezeThreshold = FastClamp(value, -90f, -30f);
        }

        /// <summary>
        /// True-peak ceiling in dBTP, -20 - 0.
        /// </summary>
        public float Ceiling
        {
            get => _ceiling;
            set => _ceiling = FastClamp(value, -20f, 0f);
        }

        /// <summary>
        /// Switches the true-peak safety limiter. The look-ahead latency stays either way.
        /// </summary>
        public bool LimiterEnabled
        {
            get => _limiterEnabled;
            set => _limiterEnabled = value;
        }

        /// <summary>
        /// Limiter look-ahead in ms, 1 - 10. This is also the effect's latency, the mixer
        /// re-aligns the tracks. Better set it up front, moving it while playing jumps the audio.
        /// </summary>
        public float Lookahead
        {
            get => _lookahead;
            set => _lookahead = FastClamp(value, 1f, 10f);
        }

        /// <summary>
        /// Limiter release in ms, 10 - 2000.
        /// </summary>
        public float LimiterRelease
        {
            get => _limiterRelease;
            set => _limiterRelease = FastClamp(value, 10f, 2000f);
        }

        /// <summary>
        /// Gain the rider starts from and returns to on Reset, in dB, -30 - +30. Setting it
        /// also moves the current gain there.
        /// </summary>
        public float InitialGain
        {
            get => _initialGain;
            set => _initialGain = FastClamp(value, -30f, 30f);
        }

        /// <summary>
        /// Latency in frames for the mixer's delay compensation.
        /// </summary>
        public int LatencySamples => _native.IsReady
            ? _native.LatencySamples
            : (int)MathF.Round(_lookahead * (_config?.SampleRate ?? DefaultSampleRate) / 1000f);

        /// <summary>
        /// Leveler gain at the end of the last processed block, in dB.
        /// Only moves while this instance is processed directly, not as a mixer twin.
        /// </summary>
        public float CurrentGainDb => ToDb(_native.GetParam(NativeEffectEngine.MeterCurrentGain) ?? DbToLinear(_initialGain));

        /// <summary>
        /// Deepest limiter gain reduction of the last processed block in dB (0 or negative).
        /// </summary>
        public float LimiterGainReductionDb => ToDb(_native.GetParam(MeterLimiterGain) ?? 1f);

        /// <summary>
        /// Gated programme loudness estimate of the input in LUFS, -120 until measured.
        /// </summary>
        public float ProgramLoudness => _native.GetParam(MeterProgramLoudness) ?? SilentLufs;

        /// <summary>
        /// Momentary (400 ms) loudness of the input in LUFS, -120 until measured.
        /// </summary>
        public float MomentaryLoudness => _native.GetParam(MeterMomentaryLoudness) ?? SilentLufs;

        /// <summary>
        /// Input sample peak of the last processed block, linear.
        /// </summary>
        public float InputLevel => _native.GetParam(NativeEffectEngine.MeterInputLevel) ?? 0f;

        #endregion

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset">The setup to load.</param>
        public void SetPreset(OwnDynamicAmpPreset preset)
        {
            // Everything goes back to square one, then the preset lays its numbers on top.
            // The positional Apply call is ugly, but twelve lines of setters per case is worse.
            Mix = 1f; LimiterEnabled = true; Ceiling = -1f; InitialGain = 0f;

            switch (preset)
            {
                case OwnDynamicAmpPreset.Speech:
                    Apply(-16f, 3f, 15f, 15f, 4f, 8f, 1f, 150f, -15f, -50f, 3f, 80f); break;
                case OwnDynamicAmpPreset.Music:
                    Apply(-14f, 12f, 9f, 9f, 1f, 2f, 1.5f, 500f, -10f, -60f, 5f, 200f); break;
                case OwnDynamicAmpPreset.Broadcast:
                    Apply(-23f, 3f, 15f, 15f, 3f, 6f, 0.5f, 200f, -10f, -55f, 5f, 100f); break;
                case OwnDynamicAmpPreset.Mastering:
                    Apply(-14f, 20f, 6f, 6f, 0.5f, 1f, 1f, 1000f, -8f, -60f, 8f, 250f); break;
                case OwnDynamicAmpPreset.Live:
                    Apply(-14f, 2f, 10f, 12f, 4f, 10f, 1f, 100f, -12f, -48f, 2f, 60f); break;
                case OwnDynamicAmpPreset.Transparent:
                    Apply(-16f, 30f, 4f, 4f, 0.3f, 0.6f, 2f, 2000f, -8f, -65f, 5f, 300f); break;
                default:
                    Apply(-14f, 8f, 12f, 12f, 1.5f, 3f, 1f, 300f, -10f, -55f, 5f, 150f); break;
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"OwnDynamicAmp: Target={_targetLoudness:F1}LUFS, Window={_window:F1}s, " +
                   $"Boost={_maxBoost:F1}dB, Cut={_maxCut:F1}dB, Tol={_tolerance:F1}dB, Ceiling={_ceiling:F1}dBTP";
        }

        private void Apply(float target, float window, float maxBoost, float maxCut, float rise, float fall,
                           float tolerance, float smoothing, float relativeGate, float freeze,
                           float lookahead, float limiterRelease)
        {
            TargetLoudness = target; Window = window; MaxBoost = maxBoost; MaxCut = maxCut;
            RiseRate = rise; FallRate = fall; Tolerance = tolerance; Smoothing = smoothing;
            RelativeGate = relativeGate; FreezeThreshold = freeze; Lookahead = lookahead;
            LimiterRelease = limiterRelease;
        }

        private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

        private static float ToDb(float linear) => linear > 1e-6f ? 20f * MathF.Log10(linear) : -120f;

        private static float FastClamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
