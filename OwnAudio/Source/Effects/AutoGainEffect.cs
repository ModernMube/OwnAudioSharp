using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Ready made AutoGain settings.
    /// </summary>
    public enum AutoGainPreset
    {
        /// <summary>
        /// General purpose, nothing fancy.
        /// </summary>
        Default,

        /// <summary>
        /// Slow and gentle, keeps musical dynamics.
        /// </summary>
        Music,

        /// <summary>
        /// Speech oriented.
        /// </summary>
        Voice,

        /// <summary>
        /// On-air style: fast, wide range, loud.
        /// </summary>
        Broadcast,

        /// <summary>
        /// Fast reacting, for stage monitoring.
        /// </summary>
        Live
    }

    /// <summary>
    /// RMS based automatic gain control. Rides the level instead of squashing it.
    /// </summary>
    public sealed class AutoGainEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig? _config;

        private float _targetLevel = 0.25f;
        private float _attackCoeff = 0.99f;
        private float _releaseCoeff = 0.999f;
        private float _gateThreshold = 0.001f;
        private float _maxGain = 4.0f;
        private float _minGain = 0.25f;

        private float _currentGain = 1.0f;
        private float _rmsLevel = 0.0f;

        /// <summary>
        /// 5ms ring, we read the delayed sample out of it.
        /// </summary>
        private float[]? _lookaheadBuffer;
        private int _lookaheadIndex;
        private int _lookaheadLength;

        private const int RmsWindowSize = 64;
        private float _rmsAccumulator = 0.0f;
        private int _rmsSampleCount = 0;

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name, falls back to "AutoGain" on null.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "AutoGain"; }

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// AutoGain has no wet/dry, so this is stuck at 1.0.
        /// </summary>
        public float Mix { get => 1.0f; set { } }

        /// <summary>
        /// RMS level we try to hold, 0.01 - 1.0.
        /// </summary>
        public float TargetLevel { get => _targetLevel; set => _targetLevel = Math.Clamp(value, 0.01f, 1.0f); }

        /// <summary>
        /// Attack smoothing, bigger = slower.
        /// </summary>
        public float AttackCoefficient { get => _attackCoeff; set => _attackCoeff = Math.Clamp(value, 0.9f, 0.999f); }

        /// <summary>
        /// Release smoothing, bigger = slower.
        /// </summary>
        public float ReleaseCoefficient { get => _releaseCoeff; set => _releaseCoeff = Math.Clamp(value, 0.9f, 0.9999f); }

        /// <summary>
        /// Below this level we stop pushing gain around.
        /// </summary>
        public float GateThreshold { get => _gateThreshold; set => _gateThreshold = Math.Clamp(value, 0.0001f, 0.01f); }

        /// <summary>
        /// Gain ceiling.
        /// </summary>
        public float MaximumGain { get => _maxGain; set => _maxGain = Math.Clamp(value, 1.0f, 10.0f); }

        /// <summary>
        /// Gain floor.
        /// </summary>
        public float MinimumGain { get => _minGain; set => _minGain = Math.Clamp(value, 0.1f, 1.0f); }

        /// <summary>
        /// Gain we are applying right now.
        /// </summary>
        public float CurrentGain => _currentGain;

        /// <summary>
        /// Detected input RMS.
        /// </summary>
        public float InputLevel => _rmsLevel;

        /// <summary>
        /// Lookahead latency in samples, the mixer uses this for PDC.
        /// 240 @48k, 220 @44.1k.
        /// </summary>
        public int LatencySamples => _lookaheadLength;

        /// <summary>
        /// Builds the effect with hand picked values.
        /// </summary>
        /// <param name="attackCoeff">Level smoothing when the signal goes up.</param>
        /// <param name="releaseCoeff">Level smoothing when it drops.</param>
        public AutoGainEffect(float targetLevel = 0.25f, float attackCoeff = 0.99f, float releaseCoeff = 0.999f,
                       float gateThreshold = 0.001f, float maxGain = 4.0f, float minGain = 0.25f)
        {
            _id = Guid.NewGuid();
            _name = "AutoGain";
            _enabled = true;
            TargetLevel = targetLevel;
            AttackCoefficient = attackCoeff;
            ReleaseCoefficient = releaseCoeff;
            GateThreshold = gateThreshold;
            MaximumGain = maxGain;
            MinimumGain = minGain;
            _initLookahead(44100);
        }

        /// <summary>
        /// Builds the effect from a preset.
        /// </summary>
        /// <param name="preset"></param>
        public AutoGainEffect(AutoGainPreset preset) : this()
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Picks up the engine sample rate and resizes the lookahead ring.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new AudioException("AutoGainEffect ERROR: ", new ArgumentNullException(nameof(config)));
            _initLookahead(config.SampleRate);
        }

        /// <summary>
        /// 5ms worth of ring buffer for the given rate.
        /// </summary>
        private void _initLookahead(int sampleRate)
        {
            int _needed = (int)(0.005f * sampleRate);
            if (_lookaheadBuffer == null || _lookaheadBuffer.Length != _needed)
                _lookaheadBuffer = new float[_needed];

            _lookaheadLength = _needed;
            _lookaheadIndex = 0;
        }

        /// <summary>
        /// Runs the gain rider over the interleaved buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled || _lookaheadBuffer == null) return;

            int sampleCount = frameCount * _config.Channels;

            float rmsLevel = _rmsLevel;
            float gain = _currentGain;
            float rmsAcc = _rmsAccumulator;
            int rmsCount = _rmsSampleCount;

            float att = _attackCoeff;
            float rel = _releaseCoeff;
            float gate = _gateThreshold;
            float target = _targetLevel;
            float maxG = _maxGain;
            float minG = _minGain;

            float invAtt = 1.0f - att;
            float invRel = 1.0f - rel;
            const float slew = 0.001f;

            for (int i = 0; i < sampleCount; i++)
            {
                float input = buffer[i];

                rmsAcc += input * input;
                rmsCount++;

                if (rmsCount >= RmsWindowSize)
                {
                    float currentRms = MathF.Sqrt(rmsAcc / RmsWindowSize);

                    if (currentRms > rmsLevel)
                        rmsLevel = att * rmsLevel + invAtt * currentRms;
                    else
                        rmsLevel = rel * rmsLevel + invRel * currentRms;

                    rmsAcc = 0.0f;
                    rmsCount = 0;
                }

                float delayed = _lookaheadBuffer[_lookaheadIndex];
                _lookaheadBuffer[_lookaheadIndex] = input;
                _lookaheadIndex++;
                if (_lookaheadIndex >= _lookaheadLength) _lookaheadIndex = 0;

                if (rmsLevel >= gate)
                {
                    float targetGain = Math.Clamp(target / Math.Max(rmsLevel, 0.0001f), minG, maxG);
                    gain += Math.Clamp(targetGain - gain, -slew, slew);
                }

                float output = delayed * gain;

                if (output > 0.95f)
                    output = 0.95f + (output - 0.95f) * 0.1f;
                else if(output < -0.95f)
                    output = -0.95f + (output + 0.95f) * 0.1f;

                buffer[i] = Math.Clamp(output, -0.99f, 0.99f);
            }

            _rmsLevel = rmsLevel;
            _currentGain = gain;
            _rmsAccumulator = rmsAcc;
            _rmsSampleCount = rmsCount;
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(AutoGainPreset preset)
        {
            switch (preset)
            {
                case AutoGainPreset.Default:
                    TargetLevel = 0.25f; AttackCoefficient = 0.990f; ReleaseCoefficient = 0.9990f;
                    MaximumGain = 4.0f; MinimumGain = 0.20f; GateThreshold = 0.001f;
                    break;
                case AutoGainPreset.Music:
                    TargetLevel = 0.20f; AttackCoefficient = 0.995f; ReleaseCoefficient = 0.9995f;
                    MaximumGain = 2.5f; MinimumGain = 0.40f; GateThreshold = 0.002f;
                    break;
                case AutoGainPreset.Voice:
                    TargetLevel = 0.28f; AttackCoefficient = 0.988f; ReleaseCoefficient = 0.9980f;
                    MaximumGain = 3.5f; MinimumGain = 0.25f; GateThreshold = 0.0015f;
                    break;
                case AutoGainPreset.Broadcast:
                    TargetLevel = 0.32f; AttackCoefficient = 0.985f; ReleaseCoefficient = 0.9950f;
                    MaximumGain = 4.0f; MinimumGain = 0.18f; GateThreshold = 0.0005f;
                    break;
                case AutoGainPreset.Live:
                    TargetLevel = 0.35f; AttackCoefficient = 0.975f; ReleaseCoefficient = 0.9920f;
                    MaximumGain = 2.5f; MinimumGain = 0.12f; GateThreshold = 0.004f;
                    break;
            }
        }

        /// <summary>
        /// Back to unity gain, empty ring.
        /// </summary>
        public void Reset()
        {
            _currentGain = 1.0f;
            _rmsLevel = 0.0f;
            _rmsAccumulator = 0.0f;
            _rmsSampleCount = 0;
            if (_lookaheadBuffer != null) Array.Clear(_lookaheadBuffer, 0, _lookaheadBuffer.Length);
            _lookaheadIndex = 0;
        }

        /// <summary>
        /// Nothing unmanaged here, we just clear the state.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            Reset();
            _disposed = true;
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString() => $"AutoGain: Target={_targetLevel:F2}, CurrentGain={_currentGain:F2}, Enabled={_enabled}";
    }
}
