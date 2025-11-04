using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Preset configurations for the enhancer effect
    /// </summary>
    public enum EnhancerPreset
    {
        /// <summary>
        /// Default preset with balanced settings for general use
        /// </summary>
        Default,

        /// <summary>
        /// Subtle enhancement for vocals - adds gentle presence without harshness
        /// Uses moderate gain (2.0x) and higher cutoff (5kHz) to enhance vocal clarity
        /// Low mix (15%) maintains naturalness while adding definition
        /// </summary>
        VocalClarity,

        /// <summary>
        /// Aggressive enhancement for rock/metal music - adds bite and edge
        /// Higher gain (4.0x) and lower cutoff (3kHz) for more pronounced effect
        /// Moderate mix (25%) provides noticeable enhancement without overpowering
        /// </summary>
        RockEdge,

        /// <summary>
        /// Clean enhancement for acoustic instruments - preserves natural tone
        /// Moderate gain (2.5x) with high cutoff (6kHz) for gentle sparkle
        /// Very low mix (10%) maintains instrument authenticity
        /// </summary>
        AcousticSparkle,

        /// <summary>
        /// Heavy enhancement for dense mixes - cuts through busy arrangements
        /// High gain (3.5x) with moderate cutoff (4kHz) for presence boost
        /// Higher mix (30%) ensures audibility in complex arrangements
        /// </summary>
        MixCutter,

        /// <summary>
        /// Broadcast-ready enhancement - professional radio/podcast processing
        /// Balanced gain (3.0x) with speech-optimized cutoff (4.5kHz)
        /// Moderate mix (20%) provides clarity without fatigue
        /// </summary>
        Broadcast
    }

    /// <summary>
    /// Enhancer effect
    /// </summary>
    public sealed class EnhancerEffect : IEffectProcessor
    {
        // IEffectProcessor implementation
        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;

        // Audio configuration
        private AudioConfig? _config;

        private float _mix;
        private float _gain;
        private float _cutFreq;
        private float _sampleRate;
        private float _alpha;
        private float _xPrev;
        private float _yPrev;

        /// <summary>
        /// Gets the unique identifier for this effect.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Gets or sets the name of the effect.
        /// </summary>
        public string Name
        {
            get => _name;
            set => _name = value ?? "Enhancer";
        }

        /// <summary>
        /// Gets or sets whether the effect is enabled.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Gets or sets the mix amount (0-1). Controls the amount of processed signal blended with the original.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Gets or sets the gain amount (0.1-10). Pre-saturation amplification.
        /// </summary>
        public float Gain
        {
            get => _gain;
            set => _gain = Math.Clamp(value, 0.1f, 10.0f);
        }

        /// <summary>
        /// Gets or sets the cutoff frequency (100-20000 Hz). High-pass filter cutoff frequency.
        /// </summary>
        public float CutoffFrequency
        {
            get => _cutFreq;
            set
            {
                _cutFreq = Math.Clamp(value, 100.0f, 20000.0f);
                UpdateFilterCoefficient();
            }
        }

        /// <summary>
        /// Gets or sets the sample rate (8000-192000 Hz). Audio system sample rate.
        /// </summary>
        public float SampleRate
        {
            get => _sampleRate;
            set
            {
                _sampleRate = Math.Clamp(value, 8000.0f, 192000.0f);
                UpdateFilterCoefficient();
            }
        }

        /// <summary>
        /// Constructor with all parameters
        /// </summary>
        /// <param name="mix">mix(0-1) : Controls the amount of processed signal blended with the original</param>
        /// <param name="cutFreq">cutoffFrequency: High-pass filter cutoff(typical 2-6kHz)</param>
        /// <param name="gain">gain: Pre-saturation amplification(typically 2-4x)</param>
        /// <param name="sampleRate">sampleRate: Audio system sample rate(typically 44.1kHz)</param>
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
        /// Constructor with preset selection
        /// </summary>
        /// <param name="preset">Preset configuration to apply</param>
        /// <param name="sampleRate">sampleRate: Audio system sample rate(typically 44.1kHz)</param>
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
        /// Initializes the effect with the specified audio configuration.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Updates the filter coefficient based on current cutoff frequency and sample rate
        /// </summary>
        private void UpdateFilterCoefficient()
        {
            if (_cutFreq > 0 && _sampleRate > 0)
            {
                float rc = 1f / (2f * MathF.PI * _cutFreq);
                _alpha = rc / (rc + 1f / (2f * MathF.PI * _cutFreq * _sampleRate));
            }
        }

        /// <summary>
        /// Enhancer process
        /// </summary>
        /// <param name="buffer">Input samples</param>
        /// <param name="frameCount">The number of frames in the buffer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled)
                return;

            // Fast path: if mix is 0, no processing needed
            if (_mix < 0.001f)
                return;

            // Calculate the actual number of samples to process
            int sampleCount = frameCount * _config.Channels;

            for (int i = 0; i < sampleCount; i++)
            {
                float original = buffer[i];

                // Apply high-pass filter
                float highFreq = _alpha * (_yPrev + original - _xPrev);
                _xPrev = original;
                _yPrev = highFreq;

                // Nonlinear processing with soft clipping
                float processed = highFreq * _gain;
                processed = MathF.Tanh(processed * 0.5f) * 2f; // Gentle saturation

                // Mix processed signal with original
                buffer[i] = original + processed * _mix;
            }
        }

        /// <summary>
        /// Apply a preset configuration optimized for specific use cases
        /// </summary>
        /// <param name="preset">The preset configuration to apply</param>
        public void SetPreset(EnhancerPreset preset)
        {
            switch (preset)
            {
                case EnhancerPreset.Default:
                    Mix = 0.2f;
                    CutoffFrequency = 4000f;
                    Gain = 2.5f;
                    break;

                case EnhancerPreset.VocalClarity:
                    Mix = 0.15f;
                    CutoffFrequency = 5000f;
                    Gain = 2.0f;
                    break;

                case EnhancerPreset.RockEdge:
                    Mix = 0.25f;
                    CutoffFrequency = 3000f;
                    Gain = 4.0f;
                    break;

                case EnhancerPreset.AcousticSparkle:
                    Mix = 0.10f;
                    CutoffFrequency = 6000f;
                    Gain = 2.5f;
                    break;

                case EnhancerPreset.MixCutter:
                    Mix = 0.30f;
                    CutoffFrequency = 4000f;
                    Gain = 3.5f;
                    break;

                case EnhancerPreset.Broadcast:
                    Mix = 0.20f;
                    CutoffFrequency = 4500f;
                    Gain = 3.0f;
                    break;

                default:
                    Mix = 0.2f;
                    CutoffFrequency = 4000f;
                    Gain = 2.5f;
                    break;
            }
        }

        /// <summary>
        /// Resets the enhancer's internal filter state by clearing previous sample values.
        /// Does not modify any settings or parameters.
        /// </summary>
        public void Reset()
        {
            _xPrev = 0.0f;
            _yPrev = 0.0f;
        }

        /// <summary>
        /// Disposes the enhancer effect and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            Reset();

            _disposed = true;
        }

        /// <summary>
        /// Returns a string representation of the effect's state.
        /// </summary>
        public override string ToString()
        {
            return $"Enhancer: Mix={_mix:F2}, Gain={_gain:F1}, Cutoff={_cutFreq:F0}Hz, Enabled={_enabled}";
        }
    }
}
