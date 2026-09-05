using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// DynamicAmp setups from speech to mastering glue.
    /// </summary>
    public enum DynamicAmpPreset
    {
        /// <summary>
        /// Transparent general purpose AGC.
        /// </summary>
        Default,

        /// <summary>
        /// Quick, with a higher gate so room noise stays down.
        /// </summary>
        Speech,

        /// <summary>
        /// Slow attack, keeps the transients.
        /// </summary>
        Music,

        /// <summary>
        /// Tight and consistent, on-air levels.
        /// </summary>
        Broadcast,

        /// <summary>
        /// Very slow glue, no audible pumping.
        /// </summary>
        Mastering,

        /// <summary>
        /// Fast for stage monitoring.
        /// </summary>
        Live,

        /// <summary>
        /// Barely does anything, just catches the drift.
        /// </summary>
        Transparent
    }

    /// <summary>
    /// Block based adaptive level control. RMS detector, gated, with a dB/sec limit on how fast the gain may move.
    /// </summary>
    public sealed class DynamicAmpEffect : NativeBackedEffect, IEffectProcessor
    {
        private float _targetRmsLevelDb;
        private float _attackTime;
        private float _releaseTime;
        private float _noiseGateThresholdDb;
        private float _maxGain;
        private float _maxGainReductionDb;
        private float _currentGain = 1.0f;
        private float _sampleRate;
        private float _rmsWindowSeconds;
        private float _maxGainChangePerSecondDb;

        /// <summary>
        /// Smoothed mean square of the input, this is what we ride.
        /// </summary>
        private float _rmsState;

        private float _lastGainDb;

        /// <summary>
        /// What the ctor primed the RMS with. Reset has to come back here, not to zero,
        /// or the AGC starts from "silence" and slams the gain on the next block.
        /// </summary>
        private float _initialRmsState;

        private float _initialGain = 1.0f;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// No dry path here, this stays at 1.0.
        /// </summary>
        public float Mix { get; set; } = 1.0f;

        /// <summary>
        /// Level we aim for in dB, -60 to -3.
        /// </summary>
        public float TargetRmsLevelDb
        {
            get => _targetRmsLevelDb;
            set => _targetRmsLevelDb = Math.Clamp(value, -60.0f, -3.0f);
        }

        /// <summary>
        /// Attack in seconds, at least 0.05.
        /// </summary>
        public float AttackTime
        {
            get => _attackTime;
            set => _attackTime = Math.Max(0.05f, value);
        }

        /// <summary>
        /// Release in seconds, at least 0.2.
        /// </summary>
        public float ReleaseTime
        {
            get => _releaseTime;
            set => _releaseTime = Math.Max(0.2f, value);
        }

        /// <summary>
        /// Gate threshold in dB, -80 to -30.
        /// </summary>
        public float NoiseGateThresholdDb
        {
            get => _noiseGateThresholdDb;
            set => _noiseGateThresholdDb = Math.Clamp(value, -80.0f, -30.0f);
        }

        /// <summary>
        /// Gain ceiling as a multiplier, 1 - 20.
        /// </summary>
        public float MaxGain
        {
            get => _maxGain;
            set => _maxGain = Math.Clamp(value, 1.0f, 20.0f);
        }

        /// <summary>
        /// How far down we are allowed to pull, in dB.
        /// </summary>
        public float MaxGainReductionDb
        {
            get => _maxGainReductionDb;
            set => _maxGainReductionDb = Math.Clamp(value, 3.0f, 40.0f);
        }

        /// <summary>
        /// RMS averaging window in seconds.
        /// </summary>
        public float RmsWindowSeconds
        {
            get => _rmsWindowSeconds;
            set => _rmsWindowSeconds = Math.Max(0.01f, value);
        }

        /// <summary>
        /// Slew limit for the gain, dB per second.
        /// </summary>
        public float MaxGainChangePerSecondDb
        {
            get => _maxGainChangePerSecondDb;
            set => _maxGainChangePerSecondDb = Math.Max(1.0f, value);
        }

        /// <summary>
        /// Gain we are applying right now, read back off the native amp. Reads the initial
        /// gain until the first Process — a mixer twin meters itself, this follows Process().
        /// </summary>
        public float CurrentGain => _native.GetParam(NativeEffectEngine.MeterCurrentGain) ?? _currentGain;

        /// <summary>
        /// The gain the rider starts from and comes back to on Reset. Internal: it is a
        /// constructor argument, not a property, and the api baseline stays put.
        /// </summary>
        internal float InitialGain => _initialGain;

        /// <summary>
        /// Builds the effect with hand picked values. The noise threshold takes dB, but a
        /// 0-1 value is accepted too and read as linear, for older callers.
        /// </summary>
        public DynamicAmpEffect(float targetLevel = -12.0f, float attackTimeSeconds = 0.5f,
                         float releaseTimeSeconds = 2.0f, float noiseThresholdDbOrLinear = -50.0f,
                         float maxGainValue = 6.0f, float sampleRateHz = 44100.0f,
                         float rmsWindowSeconds = 0.5f, float initialGain = 1.0f,
                         float maxGainChangePerSecondDb = 12.0f, float maxGainReductionDb = 12.0f)
            : base("DynamicAmp")
        {
            TargetRmsLevelDb = targetLevel;
            AttackTime = attackTimeSeconds;
            ReleaseTime = releaseTimeSeconds;

            if (noiseThresholdDbOrLinear >= 0.0f && noiseThresholdDbOrLinear <= 1.0f)
                NoiseGateThresholdDb = noiseThresholdDbOrLinear < 0.000001f ? -80.0f : LinearToDb(noiseThresholdDbOrLinear);
            else
                NoiseGateThresholdDb = noiseThresholdDbOrLinear;

            MaxGain = maxGainValue;
            MaxGainReductionDb = maxGainReductionDb;
            RmsWindowSeconds = rmsWindowSeconds;
            MaxGainChangePerSecondDb = maxGainChangePerSecondDb;
            _sampleRate = Math.Max(8000.0f, sampleRateHz);

            _currentGain = initialGain;
            _rmsState = 0.0f;
            _lastGainDb = LinearToDb(initialGain);
            _initialGain = initialGain;
            _initialRmsState = 0.0f;
        }

        /// <summary>
        /// Builds the effect from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRateHz"></param>
        /// <param name="rmsWindowSeconds"></param>
        public DynamicAmpEffect(DynamicAmpPreset preset, float sampleRateHz = 44100.0f, float rmsWindowSeconds = 0.5f)
            : base("DynamicAmp")
        {
            _sampleRate = Math.Max(8000.0f, sampleRateHz);
            RmsWindowSeconds = rmsWindowSeconds;
            _maxGainChangePerSecondDb = 12.0f;
            SetPreset(preset);

            _currentGain = 1.0f;
            _lastGainDb = 0.0f;

            float startRms = DbToLinear(-20.0f);
            _rmsState = startRms * startRms;
            _initialGain = 1.0f;
            _initialRmsState = _rmsState;
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(DynamicAmpPreset preset)
        {
            switch (preset)
            {
                case DynamicAmpPreset.Default:
                    TargetRmsLevelDb=-12.0f; AttackTime=0.30f; ReleaseTime=1.50f; NoiseGateThresholdDb=-50.0f;
                    MaxGain=6.0f; MaxGainReductionDb=12.0f; MaxGainChangePerSecondDb=12.0f; break;

                case DynamicAmpPreset.Speech:
                    TargetRmsLevelDb=-15.0f; AttackTime=0.18f; ReleaseTime=0.80f; NoiseGateThresholdDb=-45.0f;
                    MaxGain=8.0f; MaxGainReductionDb=15.0f; MaxGainChangePerSecondDb=20.0f; break;

                case DynamicAmpPreset.Music:
                    TargetRmsLevelDb=-14.0f; AttackTime=0.80f; ReleaseTime=2.50f; NoiseGateThresholdDb=-55.0f;
                    MaxGain=4.0f; MaxGainReductionDb=8.0f; MaxGainChangePerSecondDb=5.0f; break;

                case DynamicAmpPreset.Broadcast:
                    TargetRmsLevelDb=-16.0f; AttackTime=0.28f; ReleaseTime=1.40f; NoiseGateThresholdDb=-48.0f;
                    MaxGain=6.0f; MaxGainReductionDb=18.0f; MaxGainChangePerSecondDb=14.0f; break;

                case DynamicAmpPreset.Mastering:
                    TargetRmsLevelDb=-10.0f; AttackTime=2.00f; ReleaseTime=5.00f; NoiseGateThresholdDb=-60.0f;
                    MaxGain=3.0f; MaxGainReductionDb=6.0f; MaxGainChangePerSecondDb=3.0f; break;

                case DynamicAmpPreset.Live:
                    TargetRmsLevelDb=-12.0f; AttackTime=0.15f; ReleaseTime=0.80f; NoiseGateThresholdDb=-42.0f;
                    MaxGain=5.0f; MaxGainReductionDb=12.0f; MaxGainChangePerSecondDb=18.0f; break;

                case DynamicAmpPreset.Transparent:
                    TargetRmsLevelDb=-16.0f; AttackTime=3.00f; ReleaseTime=8.00f; NoiseGateThresholdDb=-65.0f;
                    MaxGain=2.5f; MaxGainReductionDb=5.0f; MaxGainChangePerSecondDb=2.0f; break;
            }
        }

        /// <summary>
        /// dB to amplitude.
        /// </summary>
        private static float DbToLinear(float db) => MathF.Pow(10.0f, db / 20.0f);

        /// <summary>
        /// Amplitude to dB, floored at -120.
        /// </summary>
        private static float LinearToDb(float linear)
        {
            if (linear <= 1e-6f) return -120.0f;
            return 20.0f * MathF.Log10(linear);
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString() => $"{_name} (ID: {Id}, Enabled: {Enabled})";

        /// <summary>
        /// Follows the engine rate.
        /// </summary>
        private protected override void OnInitialize(AudioConfig config)
        {
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
                _sampleRate = config.SampleRate;
        }

        /// <summary>
        /// The rider state the native side does not hold for us.
        /// </summary>
        private protected override void ResetState()
        {
            _currentGain = _initialGain;
            _rmsState = _initialRmsState;
            _lastGainDb = LinearToDb(_initialGain);
        }
    }
}
