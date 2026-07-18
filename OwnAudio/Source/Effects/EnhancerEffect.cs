using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Enhancer setups per source type.
    /// </summary>
    public enum EnhancerPreset
    {
        /// <summary>
        /// Balanced, works on most material.
        /// </summary>
        Default,

        /// <summary>
        /// Air above 4.5k, no harshness.
        /// </summary>
        VocalClarity,

        /// <summary>
        /// Upper mid bite for guitars.
        /// </summary>
        RockEdge,

        /// <summary>
        /// Barely there sparkle, keeps the instrument natural.
        /// </summary>
        AcousticSparkle,

        /// <summary>
        /// Presence push so the track cuts through a busy mix.
        /// </summary>
        MixCutter,

        /// <summary>
        /// Speech clarity without listening fatigue.
        /// </summary>
        Broadcast
    }

    /// <summary>
    /// Exciter: takes the high band out with a one pole HP, saturates it and adds it back on top.
    /// </summary>
    public sealed class EnhancerEffect : IEffectProcessor
    {
        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig? _config;

        private float _mix;
        private float _gain;
        private float _cutFreq;
        private float _sampleRate;

        /// <summary>
        /// HP filter coefficient, rebuilt from cutoff and rate.
        /// </summary>
        private float _alpha;
        private float _xPrev;
        private float _yPrev;

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name
        {
            get => _name;
            set => _name = value ?? "Enhancer";
        }

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// How much of the excited band we add on top, 0 - 1.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Pre saturation gain, 0.1 - 10.
        /// </summary>
        public float Gain
        {
            get => _gain;
            set => _gain = Math.Clamp(value, 0.1f, 10.0f);
        }

        /// <summary>
        /// High-pass corner, everything above this gets excited.
        /// </summary>
        public float CutoffFrequency
        {
            get => _cutFreq;
            set
            {
                _cutFreq = Math.Clamp(value, 100.0f, 20000.0f);
                _updateAlpha();
            }
        }

        /// <summary>
        /// Working sample rate.
        /// </summary>
        public float SampleRate
        {
            get => _sampleRate;
            set
            {
                _sampleRate = Math.Clamp(value, 8000.0f, 192000.0f);
                _updateAlpha();
            }
        }

        /// <summary>
        /// Builds the effect. Cutoff is usually 2-6k, gain 2-4x.
        /// </summary>
        public EnhancerEffect(float mix = 0.2f, float cutFreq = 4000f, float gain = 2.5f, float sampleRate = 44100f)
        {
            _id = Guid.NewGuid();
            _name = "Enhancer";
            _enabled = true;

            Mix = mix;
            CutoffFrequency = cutFreq;
            Gain = gain;
            SampleRate = sampleRate;
            Reset();
        }

        /// <summary>
        /// Builds the effect from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public EnhancerEffect(EnhancerPreset preset, float sampleRate = 44100f)
        {
            _id = Guid.NewGuid();
            _name = "Enhancer";
            _enabled = true;

            SampleRate = sampleRate;
            SetPreset(preset);
            Reset();
        }

        /// <summary>
        /// Stores the engine config.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// RC/(RC+dt) with RC = 1/(2pi*fc).
        /// </summary>
        private void _updateAlpha()
        {
            if(_cutFreq > 0 && _sampleRate > 0)
            {
                float rc = 1f / (2f * MathF.PI * _cutFreq);
                _alpha = rc / (rc + 1f / _sampleRate);
            }
        }

        /// <summary>
        /// Filters, saturates and adds the high band back sample by sample.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled || _mix < 0.001f) return;

            int sampleCount = frameCount * _config.Channels;

            for (int i = 0; i < sampleCount; i++)
            {
                float original = buffer[i];

                float high = _alpha * (_yPrev + original - _xPrev);
                _xPrev = original;
                _yPrev = high;

                buffer[i] = original + MathF.Tanh(high * _gain * 0.5f) * 2f * _mix;
            }
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(EnhancerPreset preset)
        {
            switch (preset)
            {
                case EnhancerPreset.VocalClarity:
                    Mix = 0.12f;
                    CutoffFrequency = 4500f;
                    Gain = 1.8f;
                    break;

                case EnhancerPreset.RockEdge:
                    Mix = 0.22f;
                    CutoffFrequency = 2800f;
                    Gain = 2.8f;
                    break;

                case EnhancerPreset.AcousticSparkle:
                    Mix = 0.08f;
                    CutoffFrequency = 5500f;
                    Gain = 1.5f;
                    break;

                case EnhancerPreset.MixCutter:
                    Mix = 0.25f;
                    CutoffFrequency = 3200f;
                    Gain = 2.8f;
                    break;

                case EnhancerPreset.Broadcast:
                    Mix = 0.18f;
                    CutoffFrequency = 4000f;
                    Gain = 2.2f;
                    break;

                default:
                    Mix = 0.15f;
                    CutoffFrequency = 3500f;
                    Gain = 1.8f;
                    break;
            }
        }

        /// <summary>
        /// Clears the filter memory, leaves the settings alone.
        /// </summary>
        public void Reset()
        {
            _xPrev = 0.0f;
            _yPrev = 0.0f;
        }

        /// <summary>
        /// Nothing unmanaged here.
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
        public override string ToString()
        {
            return $"Enhancer: Mix={_mix:F2}, Gain={_gain:F1}, Cutoff={_cutFreq:F0}Hz, Enabled={_enabled}";
        }
    }
}
