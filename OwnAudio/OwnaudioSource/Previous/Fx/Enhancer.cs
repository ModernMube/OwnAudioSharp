using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Preset configurations for the enhancer effect
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
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
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class Enhancer : SampleProcessorBase
    {
        private float _mix;
        private float _gain;
        private float _cutFreq;
        private float _sampleRate;
        private float _alpha;
        private float _xPrev;
        private float _yPrev;

        /// <summary>
        /// Constructor with all parameters
        /// </summary>
        /// <param name="mix">mix(0-1) : Controls the amount of processed signal blended with the original</param>
        /// <param name="cutFreq">cutoffFrequency: High-pass filter cutoff(typical 2-6kHz)</param>
        /// <param name="gain">gain: Pre-saturation amplification(typically 2-4x)</param>
        /// <param name="sampleRate">sampleRate: Audio system sample rate(typically 44.1kHz)</param>
        public Enhancer(float mix = 0.2f, float cutFreq = 4000f, float gain = 2.5f, float sampleRate = 44100f)
        {
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
        public Enhancer(EnhancerPreset preset, float sampleRate = 44100f)
        {
            SampleRate = sampleRate;
            SetPreset(preset);
            Reset();
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
        /// <param name="samples"></param>
        public override void Process(Span<float> samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float original = samples[i];

                // Apply high-pass filter
                float highFreq = _alpha * (_yPrev + original - _xPrev);
                _xPrev = original;
                _yPrev = highFreq;

                // Nonlinear processing with soft clipping
                float processed = highFreq * _gain;
                processed = MathF.Tanh(processed * 0.5f) * 2f; // Gentle saturation

                // Mix processed signal with original
                samples[i] = original + processed * _mix;
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
        public override void Reset()
        {
            _xPrev = 0.0f;
            _yPrev = 0.0f;
        }
    }
}
