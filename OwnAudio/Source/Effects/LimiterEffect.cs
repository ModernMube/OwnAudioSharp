using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Limiter setups per job.
    /// </summary>
    public enum LimiterPreset
    {
        /// <summary>
        /// Balanced starting point.
        /// </summary>
        Default,

        /// <summary>
        /// Transparent, only catches the true peaks.
        /// </summary>
        Mastering,

        /// <summary>
        /// Tighter and faster, consistent on-air level.
        /// </summary>
        Broadcast,

        /// <summary>
        /// Peak protection for a live rig.
        /// </summary>
        Live,

        /// <summary>
        /// Short lookahead so the drums keep their punch.
        /// </summary>
        DrumBus,

        /// <summary>
        /// Gentle with a long release, natural on vocals.
        /// </summary>
        VocalSafety,

        /// <summary>
        /// Controlled release so the low end doesn't pump.
        /// </summary>
        Bass,

        /// <summary>
        /// Slow and smooth, speech stays intelligible.
        /// </summary>
        Podcast,

        /// <summary>
        /// Heavy limiting for loud electronic material.
        /// </summary>
        Aggressive
    }

    /// <summary>
    /// Lookahead peak limiter. The DSP runs in the native engine; this type carries the
    /// parameters and reads the gain meter back off it.
    /// </summary>
    public sealed class LimiterEffect : NativeBackedEffect, IEffectProcessor
    {
        private readonly float _sampleRate;

        /// <summary>
        /// Falls back for the meters until the native limiter is up.
        /// </summary>
        private float _currentGain;

        private float _threshold;
        private float _ceiling;
        private float _release;
        private float _lookAheadMs;

        /// <summary>
        /// Look-ahead in frames, the PDC fallback before Initialize.
        /// </summary>
        private int _lookAheadFrames;

        private const float DEFAULT_THRESHOLD = -3.0f;
        private const float DEFAULT_CEILING = -0.1f;
        private const float DEFAULT_RELEASE = 50.0f;
        private const float DEFAULT_LOOKAHEAD = 5.0f;

        private const float MIN_THRESHOLD = -20.0f;
        private const float MAX_THRESHOLD = 0.0f;
        private const float MIN_CEILING = -2.0f;
        private const float MAX_CEILING = 0.0f;
        private const float MIN_RELEASE = 1.0f;
        private const float MAX_RELEASE = 1000.0f;
        private const float MIN_LOOKAHEAD = 1.0f;
        private const float MAX_LOOKAHEAD = 20.0f;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// A limiter is always fully wet, so this stays at 1.0.
        /// </summary>
        public float Mix
        {
            get => 1.0f;
            set { }
        }

        /// <summary>
        /// Lookahead latency in frames, the mixer uses this for PDC.
        /// 5ms is 240 frames at 48k, 20ms is 960.
        /// </summary>
        public int LatencySamples => _native.IsReady ? _native.LatencySamples : _lookAheadFrames;

        /// <summary>
        /// Builds the limiter with hand picked values. Threshold and ceiling are in dB,
        /// release and lookahead in ms.
        /// </summary>
        public LimiterEffect(float sampleRate, float threshold = DEFAULT_THRESHOLD,
            float ceiling = DEFAULT_CEILING, float release = DEFAULT_RELEASE,
            float lookAheadMs = DEFAULT_LOOKAHEAD)
            : base("Limiter")
        {
            _sampleRate = sampleRate;

            Threshold = threshold;
            Ceiling = ceiling;
            Release = release;

            _lookAheadMs = Math.Clamp(lookAheadMs, MIN_LOOKAHEAD, MAX_LOOKAHEAD);
            _lookAheadFrames = (int)(_lookAheadMs * sampleRate / 1000.0f);

            _currentGain = 1.0f;
        }

        /// <summary>
        /// Builds the limiter from a preset.
        /// </summary>
        /// <param name="sampleRate"></param>
        /// <param name="preset"></param>
        public LimiterEffect(float sampleRate, LimiterPreset preset)
            : this(sampleRate)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Threshold in dB, -20 to 0.
        /// </summary>
        public float Threshold
        {
            get => _linearToDb(_threshold);
            set => _threshold = _dbToLinear(Math.Clamp(value, MIN_THRESHOLD, MAX_THRESHOLD));
        }

        /// <summary>
        /// Output ceiling in dB, -2 to 0.
        /// </summary>
        public float Ceiling
        {
            get => _linearToDb(_ceiling);
            set => _ceiling = _dbToLinear(Math.Clamp(value, MIN_CEILING, MAX_CEILING));
        }

        /// <summary>
        /// Release in ms, 1 to 1000.
        /// </summary>
        public float Release
        {
            get => -1000.0f / MathF.Log(1.0f - _release) / _sampleRate;
            set => _release = _releaseCoeff(Math.Clamp(value, MIN_RELEASE, MAX_RELEASE), _sampleRate);
        }

        /// <summary>
        /// Sample rate this instance was built for.
        /// </summary>
        public float SampleRate => _sampleRate;

        /// <summary>
        /// Lookahead in ms, 1 to 20. Changing the window resets the limiter state.
        /// </summary>
        public float LookAheadMs
        {
            get => _lookAheadMs;
            set
            {
                _lookAheadMs = Math.Clamp(value, MIN_LOOKAHEAD, MAX_LOOKAHEAD);

                int newFrames = (int)(_lookAheadMs * _sampleRate / 1000.0f);
                if (newFrames != _lookAheadFrames)
                {
                    _lookAheadFrames = newFrames;
                    Reset();
                }
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"{_name} (Enabled: {Enabled}, Threshold: {Threshold:F1}dB, Ceiling: {Ceiling:F1}dB)";
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(LimiterPreset preset)
        {
            switch (preset)
            {
                case LimiterPreset.Mastering:   Threshold=-1.0f;  Ceiling=-0.1f; Release=100f; LookAheadMs=8.0f;  break;
                case LimiterPreset.Broadcast:   Threshold=-6.0f;  Ceiling=-0.3f; Release=25f;  LookAheadMs=5.0f;  break;
                case LimiterPreset.Live:        Threshold=-3.0f;  Ceiling=-0.5f; Release=50f;  LookAheadMs=3.0f;  break;
                case LimiterPreset.DrumBus:     Threshold=-2.0f;  Ceiling=-0.1f; Release=15f;  LookAheadMs=2.0f;  break;
                case LimiterPreset.VocalSafety: Threshold=-4.0f;  Ceiling=-0.2f; Release=200f; LookAheadMs=10.0f; break;
                case LimiterPreset.Bass:        Threshold=-5.0f;  Ceiling=-0.1f; Release=150f; LookAheadMs=6.0f;  break;
                case LimiterPreset.Podcast:     Threshold=-8.0f;  Ceiling=-0.5f; Release=300f; LookAheadMs=12.0f; break;
                case LimiterPreset.Aggressive:  Threshold=-10.0f; Ceiling=-0.1f; Release=10f;  LookAheadMs=3.0f;  break;

                default:
                    Threshold = DEFAULT_THRESHOLD; Ceiling = DEFAULT_CEILING;
                    Release = DEFAULT_RELEASE; LookAheadMs = DEFAULT_LOOKAHEAD;
                    break;
            }
        }

        /// <summary>
        /// Amplitude to dB.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float _linearToDb(float linear)
        {
            return 20.0f * MathF.Log10(Math.Max(linear, 1e-6f));
        }

        /// <summary>
        /// dB to amplitude.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float _dbToLinear(float db)
        {
            return MathF.Pow(10.0f, db / 20.0f);
        }

        /// <summary>
        /// One-pole release coefficient from a time in ms.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float _releaseCoeff(float timeMs, float sampleRate)
        {
            return 1.0f - MathF.Exp(-1.0f / (timeMs * sampleRate / 1000.0f));
        }

        /// <summary>
        /// Attack fast enough to finish inside the lookahead window.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float _attackCoeff(float lookAheadMs, float sampleRate)
        {
            return _releaseCoeff(Math.Max(lookAheadMs / 3.0f, 0.05f), sampleRate);
        }

        /// <summary>
        /// Current gain reduction in dB, for meters. Read off the native limiter, so it
        /// tracks what Process() actually did.
        /// </summary>
        public float GetGainReductionDb()
        {
            return 20.0f * MathF.Log10(_meteredGain);
        }

        /// <summary>
        /// True while the limiter is pulling the level down.
        /// </summary>
        public bool IsLimiting => _meteredGain < 0.99f;

        /// <summary>
        /// The native gain while there is an instance, the managed field before Initialize.
        /// </summary>
        private float _meteredGain => _native.GetParam(NativeEffectEngine.MeterCurrentGain) ?? _currentGain;

        /// <summary>
        /// The rider state the native side does not hold for us.
        /// </summary>
        private protected override void ResetState()
        {
            _currentGain = 1.0f;
        }
    }
}
