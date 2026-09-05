using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Compressor setups for the usual jobs.
    /// </summary>
    public enum CompressorPreset
    {
        /// <summary>
        /// Balanced, works on most material.
        /// </summary>
        Default,

        /// <summary>
        /// Soft optical style leveling for vocals.
        /// </summary>
        VocalGentle,

        /// <summary>
        /// Broadcast style, low threshold and high ratio.
        /// </summary>
        VocalAggressive,

        /// <summary>
        /// Very fast attack, keeps the snap.
        /// </summary>
        Drums,

        /// <summary>
        /// Tight low end glue.
        /// </summary>
        Bass,

        /// <summary>
        /// Near limiting, only catches peaks.
        /// </summary>
        MasteringLimiter,

        /// <summary>
        /// Slow, program dependent analog feel.
        /// </summary>
        Vintage
    }

    /// <summary>
    /// Soft knee peak compressor with makeup gain.
    /// </summary>
    public sealed class CompressorEffect : NativeBackedEffect, IEffectProcessor
    {
        /// <summary>
        /// Threshold window, mirrors the native compressor's clamp.
        /// </summary>
        private const float MinThresholdDb = -60.0f;
        private const float MaxThresholdDb = 0.0f;
        private const float MinThresholdLinear = 0.001f;

        private float _threshold = 0.5f;
        private float _ratio = 4.0f;
        private float _attackTime = 0.02f;
        private float _releaseTime = 0.2f;
        private float _makeupGain = 1.2f;
        private float _sampleRate = 44100f;

        private float _thresholdDb;

        /// <summary>
        /// 1/ratio - 1, the dB slope above the knee.
        /// </summary>
        private float _slope;

        /// <summary>
        /// dbx calls this OverEasy - how wide the transition into full ratio is.
        /// </summary>
        private float _kneeDb = 6.0f;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name
        {
            get => _name;
            set => _name = value ?? "Compressor";
        }

        /// <summary>
        /// Compressor is always fully wet, so this stays at 1.0.
        /// </summary>
        public float Mix
        {
            get => 1.0f;
            set { }
        }

        /// <summary>
        /// Builds the compressor: 4:1 at -6dB with a 20ms attack and a touch of makeup, the
        /// setting you'd reach for on almost anything. Attack/release come in milliseconds,
        /// threshold and makeup are linear.
        /// </summary>
        public CompressorEffect(float threshold = 0.5f, float ratio = 4.0f, float attackTime = 20f,
                         float releaseTime = 200f, float makeupGain = 1.2f, float sampleRate = 44100f)
            : base("Compressor")
        {
            //Linear floor is the -60 dB the Threshold property and the native side agree on
            _threshold = FastClamp(threshold, MinThresholdLinear, 1.0f);
            _ratio = FastClamp(ratio, 1.0f, 100.0f);
            _attackTime = FastClamp(attackTime, 0.1f, 1000f) / 1000f;
            _releaseTime = FastClamp(releaseTime, 1f, 2000f) / 1000f;
            _makeupGain = FastClamp(makeupGain, 0.1f, 10.0f);
            _sampleRate = FastClamp(sampleRate, 8000f, 192000f);

            _recalcCoeffs();
        }

        /// <summary>
        /// Builds the compressor from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public CompressorEffect(CompressorPreset preset, float sampleRate = 44100f)
            : base("Compressor")
        {
            _sampleRate = FastClamp(sampleRate, 8000f, 192000f);

            SetPreset(preset);
        }

        /// <summary>
        /// Rebuilds every cached coefficient. Call it after touching any parameter.
        /// The flag also recomputes the automatic makeup gain.
        /// </summary>
        private void _recalcCoeffs(bool isMakeUoGain = false)
        {
            _thresholdDb = 20.0f * MathF.Log10(Math.Max(_threshold, 1e-6f));
            _slope = 1.0f / _ratio - 1.0f;

            if (isMakeUoGain) _autoMakeupGain();
        }

        /// <summary>
        /// Guesses a makeup gain from threshold and ratio, assuming a -12dB average signal.
        /// Only gives back 80% of the reduction so the dynamics stay alive.
        /// </summary>
        private void _autoMakeupGain()
        {
            const float typicalInputDb = -12.0f;

            if (typicalInputDb < _thresholdDb)
            {
                _makeupGain = 1.0f;
                return;
            }

            float _grDb = _slope * (typicalInputDb - _thresholdDb) * 0.5f;
            _makeupGain = FastClamp(MathF.Pow(10.0f, (-_grDb * 0.8f) / 20.0f), 0.1f, 10.0f);
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(CompressorPreset preset)
        {
            Reset();
            switch (preset)
            {
                case CompressorPreset.Default:          _threshold=0.50f; _ratio=4.0f;  _attackTime=0.020f;  _releaseTime=0.200f; _makeupGain=1.2f; break;
                case CompressorPreset.VocalGentle:      _threshold=0.65f; _ratio=2.8f;  _attackTime=0.018f;  _releaseTime=0.180f; _makeupGain=1.3f; break;
                case CompressorPreset.VocalAggressive:  _threshold=0.35f; _ratio=8.0f;  _attackTime=0.005f;  _releaseTime=0.100f; _makeupGain=2.5f; break;
                case CompressorPreset.Drums:            _threshold=0.55f; _ratio=5.0f;  _attackTime=0.001f;  _releaseTime=0.060f; _makeupGain=2.0f; break;
                case CompressorPreset.Bass:             _threshold=0.42f; _ratio=6.0f;  _attackTime=0.010f;  _releaseTime=0.200f; _makeupGain=2.0f; break;
                case CompressorPreset.MasteringLimiter: _threshold=0.65f; _ratio=20.0f; _attackTime=0.0001f; _releaseTime=0.080f; _makeupGain=1.0f; break;
                case CompressorPreset.Vintage:          _threshold=0.52f; _ratio=3.5f;  _attackTime=0.025f;  _releaseTime=0.350f; _makeupGain=1.7f; break;
            }
            _recalcCoeffs(false);
        }

        /// <summary>
        /// Threshold in dB, -60 to 0 — same window the native compressor clamps to, so what you
        /// read back is what the engine actually runs.
        /// </summary>
        public float Threshold
        {
            get => LinearToDb(_threshold);
            set
            {
                float newLinear = DbToLinear(FastClamp(value, MinThresholdDb, MaxThresholdDb));
                if (Math.Abs(_threshold - newLinear) > 0.000001f)
                {
                    _threshold = newLinear;
                    _recalcCoeffs(true);
                }
            }
        }

        /// <summary>
        /// Compression ratio, N:1.
        /// </summary>
        public float Ratio
        {
            get => _ratio;
            set
            {
                if (Math.Abs(_ratio - value) > 0.01f)
                {
                    _ratio = FastClamp(value, 1.0f, 100.0f);
                    _recalcCoeffs(true);
                }
            }
        }

        /// <summary>
        /// Attack in ms.
        /// </summary>
        public float AttackTime
        {
            get => _attackTime * 1000f;
            set
            {
                float newTime = FastClamp(value, 0.1f, 1000f) / 1000f;
                if (Math.Abs(_attackTime - newTime) > 0.00001f)
                {
                    _attackTime = newTime;
                    _recalcCoeffs(true);
                }
            }
        }

        /// <summary>
        /// Release in ms.
        /// </summary>
        public float ReleaseTime
        {
            get => _releaseTime * 1000f;
            set
            {
                float newTime = FastClamp(value, 1f, 2000f) / 1000f;
                if (Math.Abs(_releaseTime - newTime) > 0.00001f)
                {
                    _releaseTime = newTime;
                    _recalcCoeffs(true);
                }
            }
        }

        /// <summary>
        /// Soft knee width in dB, 0 is hard knee. dbx OverEasy in everything but name.
        /// </summary>
        public float KneeWidth
        {
            get => _kneeDb;
            set
            {
                _kneeDb = FastClamp(value, 0.0f, 24.0f);
                _recalcCoeffs();
            }
        }

        /// <summary>
        /// Makeup gain in dB.
        /// </summary>
        public float MakeupGain
        {
            get => LinearToDb(_makeupGain);
            set => _makeupGain = FastClamp(DbToLinear(value), 0.1f, 10.0f);
        }

        /// <summary>
        /// Working sample rate.
        /// </summary>
        public float SampleRate
        {
            get => _sampleRate;
            set
            {
                if (Math.Abs(_sampleRate - value) > 1.0f)
                {
                    _sampleRate = FastClamp(value, 8000f, 192000f);
                    _recalcCoeffs(true);
                }
            }
        }

        /// <summary>
        /// Amplitude to dB.
        /// </summary>
        public static float LinearToDb(float linear)
        {
            return 20f * MathF.Log10(Math.Max(linear, 1e-6f));
        }

        /// <summary>
        /// dB back to amplitude.
        /// </summary>
        public static float DbToLinear(float dB)
        {
            return MathF.Pow(10f, dB / 20f);
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"Compressor: Threshold={_threshold:F2}, Ratio={_ratio:F1}:1, Attack={AttackTime:F1}ms, Release={ReleaseTime:F1}ms, Enabled={Enabled}";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastClamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        /// <summary>
        /// Follows the engine rate.
        /// </summary>
        private protected override void OnInitialize(AudioConfig config)
        {
            if (Math.Abs(_sampleRate - config.SampleRate) > 0.1f)
            {
                _sampleRate = config.SampleRate;
                _recalcCoeffs();
            }
        }
    }
}
