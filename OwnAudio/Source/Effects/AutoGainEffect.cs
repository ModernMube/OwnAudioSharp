using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.Effects
{
    public enum AutoGainPreset
    {
        Default,
        Music,
        Voice,
        Broadcast,
        Live
    }

    public sealed class AutoGainEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig? _config;

        // Parameters
        private float _targetLevel = 0.25f;
        private float _attackCoeff = 0.99f;
        private float _releaseCoeff = 0.999f;
        private float _gateThreshold = 0.001f;
        private float _maxGain = 4.0f;
        private float _minGain = 0.25f;

        // State
        private float _currentGain = 1.0f;
        private float _currentLevel = 0.0f;
        private float[]? _lookaheadBuffer;
        private int _lookaheadIndex;
        private int _lookaheadLength;

        public Guid Id => _id;
        public string Name { get => _name; set => _name = value ?? "AutoGain"; }
        public bool Enabled { get => _enabled; set => _enabled = value; }
        public float Mix { get => 1.0f; set { } } // Always 100%

        // Properties with validation
        public float TargetLevel { get => _targetLevel; set => _targetLevel = Math.Clamp(value, 0.01f, 1.0f); }
        public float AttackCoefficient { get => _attackCoeff; set => _attackCoeff = Math.Clamp(value, 0.9f, 0.999f); }
        public float ReleaseCoefficient { get => _releaseCoeff; set => _releaseCoeff = Math.Clamp(value, 0.9f, 0.9999f); }
        public float GateThreshold { get => _gateThreshold; set => _gateThreshold = Math.Clamp(value, 0.0001f, 0.01f); }
        public float MaximumGain { get => _maxGain; set => _maxGain = Math.Clamp(value, 1.0f, 10.0f); }
        public float MinimumGain { get => _minGain; set => _minGain = Math.Clamp(value, 0.1f, 1.0f); }
        
        public float CurrentGain => _currentGain;
        public float InputLevel => _currentLevel;

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

        public AutoGainEffect(AutoGainPreset preset) : this()
        {
            SetPreset(preset);
        }

        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new AudioException("AutoGainEffect ERROR: ", new ArgumentNullException(nameof(config)));
            InitializeLookahead(config.SampleRate);
        }

        private void InitializeLookahead(int sampleRate)
        {
            // 5ms lookahead is enough to catch transients
            int needed = (int)(0.005f * sampleRate);
            if (_lookaheadBuffer == null || _lookaheadBuffer.Length != needed)
            {
                _lookaheadBuffer = new float[needed];
            }
            _lookaheadLength = needed;
            _lookaheadIndex = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled || _lookaheadBuffer == null) return;

            int sampleCount = frameCount * _config.Channels;

            // Local cache
            float level = _currentLevel;
            float gain = _currentGain;
            float att = _attackCoeff;
            float rel = _releaseCoeff;
            float gate = _gateThreshold;
            float target = _targetLevel;
            float maxG = _maxGain;
            float minG = _minGain;
            
            // Optimization: Pre-calculate smoothing factor (e.g. 1-att)?
            float invAtt = 1.0f - att;
            float invRel = 1.0f - rel;

            for (int i = 0; i < sampleCount; i++)
            {
                float input = buffer[i];
                float inputAbs = Math.Abs(input);

                // Lookahead Logic
                float bufOut = _lookaheadBuffer[_lookaheadIndex];
                _lookaheadBuffer[_lookaheadIndex] = input;
                _lookaheadIndex++;
                if (_lookaheadIndex >= _lookaheadLength) _lookaheadIndex = 0;
                
                // Detection on INPUT (Ahead of output)
                // This allows gain to drop BEFORE the loud main signal hits the output
                if (inputAbs > level)
                    level = att * level + invAtt * inputAbs;
                else
                    level = rel * level + invRel * inputAbs;

                // Gate
                if (level < gate)
                {
                    // If below gate, hold current gain or drift to unity?
                    // Typically hold or slow release. 
                    // Let's just apply current gain.
                }
                else
                {
                    // Calculate Target Gain
                    // targetGain = TargetLevel / Level
                    // Prevent division by zero roughly
                    float effectiveLevel = level > 0.0001f ? level : 0.0001f;
                    float targetGainVal = target / effectiveLevel;
                    
                    // Clamp
                    if (targetGainVal > maxG) targetGainVal = maxG;
                    if (targetGainVal < minG) targetGainVal = minG;
                    
                    // Smooth Gain Transition (Fast attack, slow release? Or just smooth?)
                    // 0.995 is roughly 200 samples at 44k (~5ms)
                    gain = 0.995f * gain + 0.005f * targetGainVal;
                }

                // Apply Gain to DELAYED signal
                float output = bufOut * gain;

                // Soft Limiting
                if (output > 0.98f) output = 0.98f;
                else if (output < -0.98f) output = -0.98f;

                buffer[i] = output;
            }

            _currentLevel = level;
            _currentGain = gain;
        }

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

        public void Reset()
        {
            _currentGain = 1.0f;
            _currentLevel = 0.0f;
            if (_lookaheadBuffer != null) Array.Clear(_lookaheadBuffer, 0, _lookaheadBuffer.Length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            Reset();
            _disposed = true;
        }

        public override string ToString() => $"AutoGain: Target={_targetLevel:F2}, CurrentGain={_currentGain:F2}, Enabled={_enabled}";
    }
}
