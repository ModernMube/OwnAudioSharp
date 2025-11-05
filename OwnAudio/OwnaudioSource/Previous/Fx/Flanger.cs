using OwnaudioLegacy.Processors;
using System;

namespace OwnaudioLegacy.Fx
{
    /// <summary>
    /// Flanger presets for different audio processing scenarios
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public enum FlangerPreset
    {
        /// <summary>
        /// Default flanger settings - balanced all-purpose flanging effect
        /// Moderate rate and depth with controlled feedback for general use
        /// </summary>
        Default,

        /// <summary>
        /// Classic flanger - vintage tape-style flanging effect
        /// Moderate rate and depth with balanced feedback for that classic swoosh
        /// </summary>
        Classic,

        /// <summary>
        /// Jet plane - fast, dramatic flanging reminiscent of aircraft sounds
        /// High rate and depth with strong feedback for aggressive modulation
        /// </summary>
        JetPlane,

        /// <summary>
        /// Subtle chorus - gentle modulation for thickening without obvious effect
        /// Slow rate, shallow depth, minimal feedback for transparent enhancement
        /// </summary>
        SubtleChorus,

        /// <summary>
        /// Vocal doubling - creates natural vocal doubling effect
        /// Medium-slow rate, moderate depth, low feedback for vocal enhancement
        /// </summary>
        VocalDoubling,

        /// <summary>
        /// Guitar lead - dramatic lead guitar flanging for solos
        /// Variable rate, deep modulation, high feedback for cutting through mix
        /// </summary>
        GuitarLead,

        /// <summary>
        /// Ambient wash - slow, ethereal flanging for atmospheric effects
        /// Very slow rate, deep modulation, moderate feedback for dreamy textures
        /// </summary>
        AmbientWash,

        /// <summary>
        /// Percussive flanger - tight, rhythmic flanging for drums and percussion
        /// Fast rate, moderate depth, low feedback to preserve transients
        /// </summary>
        Percussive
    }

    /// <summary>
    /// Flanger effect with variable delay modulation
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class Flanger : SampleProcessorBase
    {
        private readonly float[] _delayBuffer;
        private readonly int _sampleRate;
        private int _bufferIndex;

        private float _rate = 0.5f;
        private float _depth = 0.8f;
        private float _feedback = 0.6f;
        private float _mix = 0.5f;

        private float _lfoPhase = 0.0f;

        /// <summary>
        /// LFO rate in Hz (0.1 - 5.0).
        /// </summary>
        public float Rate
        {
            get => _rate;
            set => _rate = Math.Clamp(value, 0.1f, 5.0f);
        }

        /// <summary>
        /// Modulation depth (0.0 - 1.0).
        /// </summary>
        public float Depth
        {
            get => _depth;
            set => _depth = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Feedback amount (0.0 - 0.95).
        /// </summary>
        public float Feedback
        {
            get => _feedback;
            set => _feedback = Math.Clamp(value, 0.0f, 0.95f);
        }

        /// <summary>
        /// Mix between dry and wet signal (0.0 - 1.0).
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Initialize Flanger Processor with specific parameters.
        /// </summary>
        /// <param name="rate">LFO rate in Hz (0.1 - 5.0)</param>
        /// <param name="depth">Modulation depth (0.0 - 1.0)</param>
        /// <param name="feedback">Feedback amount (0.0 - 0.95)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="sampleRate">Sample rate</param>
        public Flanger(float rate = 0.5f, float depth = 0.8f, float feedback = 0.6f, float mix = 0.5f, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;
            Rate = rate;
            Depth = depth;
            Feedback = feedback;
            Mix = mix;

            int maxDelaySamples = (int)(0.02 * sampleRate);
            _delayBuffer = new float[maxDelaySamples];
            _bufferIndex = 0;
        }

        /// <summary>
        /// Initialize Flanger Processor with a preset.
        /// </summary>
        /// <param name="preset">Flanger preset to use</param>
        /// <param name="sampleRate">Sample rate</param>
        public Flanger(FlangerPreset preset, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;

            int maxDelaySamples = (int)(0.02 * sampleRate);
            _delayBuffer = new float[maxDelaySamples];
            _bufferIndex = 0;

            SetPreset(preset);
        }

        /// <summary>
        /// Set flanger parameters using predefined presets
        /// </summary>
        public void SetPreset(FlangerPreset preset)
        {
            switch (preset)
            {
                case FlangerPreset.Default:
                    // Default balanced flanger settings - good starting point
                    // Moderate settings that work well for most applications
                    Rate = 0.5f;         // Default moderate sweep speed
                    Depth = 0.8f;        // Default good modulation depth
                    Feedback = 0.6f;     // Default balanced feedback
                    Mix = 0.5f;          // Default 50/50 mix
                    break;

                case FlangerPreset.Classic:
                    // Classic tape flanger sound - balanced and musical
                    // Medium rate for classic swoosh, good depth for character
                    Rate = 0.7f;         // Classic moderate sweep speed
                    Depth = 0.75f;       // Strong but not overwhelming modulation
                    Feedback = 0.65f;    // Characteristic resonance without harshness
                    Mix = 0.45f;         // Slightly dry-focused for musical balance
                    break;

                case FlangerPreset.JetPlane:
                    // Aggressive jet plane flanging - dramatic and intense
                    // Fast rate and deep modulation with strong feedback
                    Rate = 2.8f;         // Fast sweep for aircraft-like effect
                    Depth = 0.95f;       // Maximum modulation depth
                    Feedback = 0.85f;    // High feedback for dramatic resonance
                    Mix = 0.65f;         // Wet-focused for obvious effect
                    break;

                case FlangerPreset.SubtleChorus:
                    // Gentle thickening effect - transparent and natural
                    // Slow rate with shallow depth for chorus-like enhancement
                    Rate = 0.25f;        // Slow, gentle modulation
                    Depth = 0.35f;       // Shallow depth for subtlety
                    Feedback = 0.25f;    // Minimal feedback to avoid obvious flanging
                    Mix = 0.30f;         // Light wet signal for transparency
                    break;

                case FlangerPreset.VocalDoubling:
                    // Natural vocal doubling - enhances without distraction
                    // Medium-slow rate with controlled depth and feedback
                    Rate = 0.4f;         // Slow enough to feel natural
                    Depth = 0.55f;       // Moderate depth for presence
                    Feedback = 0.35f;    // Low feedback to maintain clarity
                    Mix = 0.35f;         // Balanced but dry-focused
                    break;

                case FlangerPreset.GuitarLead:
                    // Cutting lead guitar flanger - dramatic and present
                    // Variable rate with deep modulation for solos
                    Rate = 1.5f;         // Medium-fast for movement without blur
                    Depth = 0.85f;       // Deep modulation for character
                    Feedback = 0.75f;    // Strong feedback for cutting through mix
                    Mix = 0.55f;         // Wet-focused for effect prominence
                    break;

                case FlangerPreset.AmbientWash:
                    // Ethereal atmospheric flanging - dreamy and spacious
                    // Very slow rate with deep modulation for texture
                    Rate = 0.15f;        // Very slow for dreamy movement
                    Depth = 0.90f;       // Deep modulation for texture
                    Feedback = 0.55f;    // Moderate feedback for richness
                    Mix = 0.60f;         // Wet-focused for atmospheric effect
                    break;

                case FlangerPreset.Percussive:
                    // Tight percussive flanging - preserves transients
                    // Fast rate with controlled depth to maintain punch
                    Rate = 3.5f;         // Fast rate for rhythmic effect
                    Depth = 0.60f;       // Moderate depth to preserve attack
                    Feedback = 0.40f;    // Low feedback to maintain clarity
                    Mix = 0.40f;         // Balanced but preserving dry signal
                    break;
            }
        }

        /// <summary>
        /// Process samples with flanger effect.
        /// </summary>
        /// <param name="samples">Input samples</param>
        public override void Process(Span<float> samples)
        {
            float lfoIncrement = (float)(2.0 * Math.PI * Rate / _sampleRate);

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                float lfoValue = (float)Math.Sin(_lfoPhase);

                float delayTime = 0.001f + (0.009f * (1.0f + lfoValue * Depth) * 0.5f);
                int delaySamples = (int)(delayTime * _sampleRate);
                delaySamples = Math.Clamp(delaySamples, 1, _delayBuffer.Length - 1);

                int readIndex = (_bufferIndex - delaySamples + _delayBuffer.Length) % _delayBuffer.Length;
                float delayedSample = _delayBuffer[readIndex];

                float feedbackSample = input + (delayedSample * Feedback);

                _delayBuffer[_bufferIndex] = Math.Clamp(feedbackSample, -1.0f, 1.0f);

                samples[i] = (input * (1.0f - Mix)) + (delayedSample * Mix);

                _bufferIndex = (_bufferIndex + 1) % _delayBuffer.Length;
                _lfoPhase += lfoIncrement;

                if (_lfoPhase >= 2.0 * Math.PI)
                    _lfoPhase -= (float)(2.0 * Math.PI);
            }
        }

        /// <summary>
        /// Reset flanger effect state.
        /// </summary>
        public override void Reset()
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _bufferIndex = 0;
            _lfoPhase = 0.0f;
        }
    }
}
