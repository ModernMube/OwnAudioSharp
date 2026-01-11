using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Preset configurations for the AutoGainEffect.
    /// </summary>
    public enum AutoGainPreset
    {
        /// <summary>
        /// Balanced settings suitable for general use.
        /// </summary>
        Default,
        
        /// <summary>
        /// Gentle settings optimized for music with slower response and moderate gain range.
        /// </summary>
        Music,
        
        /// <summary>
        /// Settings optimized for voice with moderate target level and gain range.
        /// </summary>
        Voice,
        
        /// <summary>
        /// Aggressive settings for broadcast with fast response and wide gain range.
        /// </summary>
        Broadcast,
        
        /// <summary>
        /// Fast response settings for live performance with higher target level.
        /// </summary>
        Live
    }

    /// <summary>
    /// Professional automatic gain control effect using RMS-based detection for musical and unobtrusive level management.
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
        private float[]? _lookaheadBuffer;
        private int _lookaheadIndex;
        private int _lookaheadLength;
        private const int RmsWindowSize = 64;
        private float _rmsAccumulator = 0.0f;
        private int _rmsSampleCount = 0;

        /// <summary>
        /// Gets the unique identifier for this effect instance.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Gets or sets the name of this effect instance.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "AutoGain"; }

        /// <summary>
        /// Gets or sets whether this effect is enabled.
        /// </summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// Gets the mix level. Always returns 1.0 (100%) as AutoGain does not support wet/dry mixing.
        /// </summary>
        public float Mix { get => 1.0f; set { } }

        /// <summary>
        /// Gets or sets the target RMS level (0.01 to 1.0). The effect will adjust gain to maintain this level.
        /// </summary>
        public float TargetLevel { get => _targetLevel; set => _targetLevel = Math.Clamp(value, 0.01f, 1.0f); }

        /// <summary>
        /// Gets or sets the attack coefficient (0.9 to 0.999). Higher values = slower attack.
        /// </summary>
        public float AttackCoefficient { get => _attackCoeff; set => _attackCoeff = Math.Clamp(value, 0.9f, 0.999f); }

        /// <summary>
        /// Gets or sets the release coefficient (0.9 to 0.9999). Higher values = slower release.
        /// </summary>
        public float ReleaseCoefficient { get => _releaseCoeff; set => _releaseCoeff = Math.Clamp(value, 0.9f, 0.9999f); }

        /// <summary>
        /// Gets or sets the noise gate threshold (0.0001 to 0.01). Signals below this level will not trigger gain changes.
        /// </summary>
        public float GateThreshold { get => _gateThreshold; set => _gateThreshold = Math.Clamp(value, 0.0001f, 0.01f); }

        /// <summary>
        /// Gets or sets the maximum gain multiplier (1.0 to 10.0).
        /// </summary>
        public float MaximumGain { get => _maxGain; set => _maxGain = Math.Clamp(value, 1.0f, 10.0f); }

        /// <summary>
        /// Gets or sets the minimum gain multiplier (0.1 to 1.0).
        /// </summary>
        public float MinimumGain { get => _minGain; set => _minGain = Math.Clamp(value, 0.1f, 1.0f); }

        /// <summary>
        /// Gets the current gain being applied to the signal.
        /// </summary>
        public float CurrentGain => _currentGain;

        /// <summary>
        /// Gets the current detected RMS level of the input signal.
        /// </summary>
        public float InputLevel => _rmsLevel;

        /// <summary>
        /// Initializes a new instance of the AutoGainEffect with custom parameters.
        /// </summary>
        /// <param name="targetLevel">Target RMS level (default: 0.25).</param>
        /// <param name="attackCoeff">Attack coefficient (default: 0.99).</param>
        /// <param name="releaseCoeff">Release coefficient (default: 0.999).</param>
        /// <param name="gateThreshold">Noise gate threshold (default: 0.001).</param>
        /// <param name="maxGain">Maximum gain multiplier (default: 4.0).</param>
        /// <param name="minGain">Minimum gain multiplier (default: 0.25).</param>
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
            InitializeLookahead(44100);
        }

        /// <summary>
        /// Initializes a new instance of the AutoGainEffect using a preset configuration.
        /// </summary>
        /// <param name="preset">The preset configuration to use.</param>
        public AutoGainEffect(AutoGainPreset preset) : this()
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Initializes the effect with the specified audio configuration.
        /// </summary>
        /// <param name="config">The audio configuration.</param>
        /// <exception cref="AudioException">Thrown when config is null.</exception>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new AudioException("AutoGainEffect ERROR: ", new ArgumentNullException(nameof(config)));
            InitializeLookahead(config.SampleRate);
        }

        /// <summary>
        /// Initializes the lookahead buffer based on the sample rate.
        /// </summary>
        /// <param name="sampleRate">The audio sample rate.</param>
        private void InitializeLookahead(int sampleRate)
        {
            int needed = (int)(0.005f * sampleRate); // 5ms lookahead
            if (_lookaheadBuffer == null || _lookaheadBuffer.Length != needed)
            {
                _lookaheadBuffer = new float[needed];
            }
            _lookaheadLength = needed;
            _lookaheadIndex = 0;
        }

        /// <summary>
        /// Processes the audio buffer with automatic gain control.
        /// </summary>
        /// <param name="buffer">The audio buffer to process.</param>
        /// <param name="frameCount">The number of frames in the buffer.</param>
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
            const float gainSlewRate = 0.001f; // Smooth gain changes

            for (int i = 0; i < sampleCount; i++)
            {
                float input = buffer[i];
                
                // RMS calculation
                rmsAcc += input * input;
                rmsCount++;
                
                if (rmsCount >= RmsWindowSize)
                {
                    float currentRms = MathF.Sqrt(rmsAcc / RmsWindowSize);
                    
                    // Smooth RMS with attack/release
                    if (currentRms > rmsLevel)
                        rmsLevel = att * rmsLevel + invAtt * currentRms;
                    else
                        rmsLevel = rel * rmsLevel + invRel * currentRms;
                    
                    rmsAcc = 0.0f;
                    rmsCount = 0;
                }

                // Lookahead buffer management
                float delayedSample = _lookaheadBuffer[_lookaheadIndex];
                _lookaheadBuffer[_lookaheadIndex] = input;
                _lookaheadIndex++;
                if (_lookaheadIndex >= _lookaheadLength) _lookaheadIndex = 0;

                // Gain calculation with noise gate
                if (rmsLevel >= gate)
                {
                    float effectiveLevel = Math.Max(rmsLevel, 0.0001f);
                    float targetGain = Math.Clamp(target / effectiveLevel, minG, maxG);
                    
                    // Smooth gain transition with slew limiting
                    float gainDiff = targetGain - gain;
                    gain += Math.Clamp(gainDiff, -gainSlewRate, gainSlewRate);
                }

                // Apply gain to delayed signal
                float output = delayedSample * gain;

                // Soft limiting using tanh-like curve
                if (output > 0.95f)
                    output = 0.95f + (output - 0.95f) * 0.1f;
                else if (output < -0.95f)
                    output = -0.95f + (output + 0.95f) * 0.1f;
                
                output = Math.Clamp(output, -0.99f, 0.99f);

                buffer[i] = output;
            }

            _rmsLevel = rmsLevel;
            _currentGain = gain;
            _rmsAccumulator = rmsAcc;
            _rmsSampleCount = rmsCount;
        }

        /// <summary>
        /// Applies a preset configuration to the effect.
        /// </summary>
        /// <param name="preset">The preset to apply.</param>
        public void SetPreset(AutoGainPreset preset)
        {
            switch (preset)
            {
                case AutoGainPreset.Default:
                    TargetLevel = 0.25f; AttackCoefficient = 0.99f; ReleaseCoefficient = 0.999f;
                    MaximumGain = 4.0f; MinimumGain = 0.25f; GateThreshold = 0.001f;
                    break;
                case AutoGainPreset.Music:
                    TargetLevel = 0.2f; AttackCoefficient = 0.995f; ReleaseCoefficient = 0.9995f;
                    MaximumGain = 2.0f; MinimumGain = 0.5f; GateThreshold = 0.002f;
                    break;
                case AutoGainPreset.Voice:
                    TargetLevel = 0.3f; AttackCoefficient = 0.99f; ReleaseCoefficient = 0.999f;
                    MaximumGain = 3.0f; MinimumGain = 0.3f; GateThreshold = 0.001f;
                    break;
                case AutoGainPreset.Broadcast:
                    TargetLevel = 0.4f; AttackCoefficient = 0.98f; ReleaseCoefficient = 0.995f;
                    MaximumGain = 4.0f; MinimumGain = 0.2f; GateThreshold = 0.0005f;
                    break;
                case AutoGainPreset.Live:
                    TargetLevel = 0.5f; AttackCoefficient = 0.97f; ReleaseCoefficient = 0.99f;
                    MaximumGain = 2.5f; MinimumGain = 0.1f; GateThreshold = 0.005f;
                    break;
            }
        }

        /// <summary>
        /// Resets the effect state to initial values.
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
        /// Disposes the effect and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            Reset();
            _disposed = true;
        }

        /// <summary>
        /// Returns a string representation of the effect's current state.
        /// </summary>
        /// <returns>A string describing the effect state.</returns>
        public override string ToString() => $"AutoGain: Target={_targetLevel:F2}, CurrentGain={_currentGain:F2}, Enabled={_enabled}";
    }
}
