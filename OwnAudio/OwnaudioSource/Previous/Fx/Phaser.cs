using OwnaudioLegacy.Processors;
using System;

namespace OwnaudioLegacy.Fx
{
    /// <summary>
    /// Phaser presets for different audio processing scenarios
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public enum PhaserPreset
    {
        /// <summary>
        /// Default phaser settings - balanced parameters for general use
        /// Medium rate, moderate depth, balanced feedback and mix
        /// </summary>
        Default,

        /// <summary>
        /// Classic vintage phaser - warm, musical sweep reminiscent of 70s rock
        /// Moderate rate, deep modulation, medium feedback for that classic "swoosh"
        /// </summary>
        Vintage,

        /// <summary>
        /// Subtle ambient phaser - gentle movement for atmospheric textures
        /// Slow rate, shallow depth, minimal feedback for background ambience
        /// </summary>
        Ambient,

        /// <summary>
        /// Fast tremolo-like phaser - rapid modulation for rhythmic effects
        /// High rate, moderate depth, low feedback for pulsing rhythmic texture
        /// </summary>
        Tremolo,

        /// <summary>
        /// Deep space phaser - dramatic sweeps for psychedelic and experimental sounds
        /// Slow rate, maximum depth, high feedback for dramatic swooshes
        /// </summary>
        DeepSpace,

        /// <summary>
        /// Guitar solo phaser - classic lead guitar phasing effect
        /// Medium-fast rate, good depth, moderate feedback for cutting through mix
        /// </summary>
        GuitarSolo,

        /// <summary>
        /// Vocal phaser - gentle enhancement for vocal tracks
        /// Slow rate, light depth, minimal feedback to add character without distraction
        /// </summary>
        Vocal,

        /// <summary>
        /// Synth pad phaser - lush modulation for synthesizer pads and strings
        /// Medium rate, deep modulation, high feedback for evolving textures
        /// </summary>
        SynthPad
    }

    /// <summary>
    /// Phaser effect with all-pass filter stages
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class Phaser : SampleProcessorBase
    {
        private readonly int _sampleRate;
        private readonly AllPassFilter[] _allPassFilters;

        private float _rate = 0.5f;
        private float _depth = 0.7f;
        private float _feedback = 0.5f;
        private float _mix = 0.5f;
        private int _stages = 4;

        private float _lfoPhase = 0.0f;

        /// <summary>
        /// LFO rate in Hz (0.1 - 10.0).
        /// </summary>
        public float Rate
        {
            get => _rate;
            set => _rate = Math.Clamp(value, 0.1f, 10.0f);
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
        /// Number of all-pass filter stages (2 - 8).
        /// </summary>
        public int Stages
        {
            get => _stages;
            set => _stages = Math.Clamp(value, 2, 8);
        }

        /// <summary>
        /// Initialize Phaser Processor with all parameters.
        /// </summary>
        /// <param name="rate">LFO rate in Hz (0.1 - 10.0)</param>
        /// <param name="depth">Modulation depth (0.0 - 1.0)</param>
        /// <param name="feedback">Feedback amount (0.0 - 0.95)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="stages">Number of stages (2 - 8)</param>
        /// <param name="sampleRate">Sample rate</param>
        public Phaser(float rate = 0.5f, float depth = 0.7f, float feedback = 0.5f, float mix = 0.5f, int stages = 4, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;
            Rate = rate;
            Depth = depth;
            Feedback = feedback;
            Mix = mix;
            Stages = stages;

            _allPassFilters = new AllPassFilter[8]; // Maximum stages
            for (int i = 0; i < _allPassFilters.Length; i++)
            {
                _allPassFilters[i] = new AllPassFilter();
            }
        }

        /// <summary>
        /// Initialize Phaser Processor with preset selection.
        /// </summary>
        /// <param name="preset">Preset to use</param>
        /// <param name="sampleRate">Sample rate</param>
        public Phaser(PhaserPreset preset, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;

            _allPassFilters = new AllPassFilter[8]; // Maximum stages
            for (int i = 0; i < _allPassFilters.Length; i++)
            {
                _allPassFilters[i] = new AllPassFilter();
            }

            SetPreset(preset);
        }

        /// <summary>
        /// Set phaser parameters using predefined presets
        /// </summary>
        public void SetPreset(PhaserPreset preset)
        {
            switch (preset)
            {
                case PhaserPreset.Default:
                    // Default balanced settings for general use
                    Rate = 0.5f;        // 0.5 Hz - moderate sweep speed
                    Depth = 0.7f;       // Good depth for noticeable effect
                    Feedback = 0.5f;    // Balanced feedback
                    Mix = 0.5f;         // Equal dry/wet mix
                    Stages = 4;         // Standard 4-stage configuration
                    break;

                case PhaserPreset.Vintage:
                    // Classic 70s rock phaser sound - warm and musical
                    // Medium rate for that classic sweep, good depth for character
                    Rate = 0.6f;        // 0.6 Hz - classic sweep speed
                    Depth = 0.8f;       // Deep modulation for pronounced effect
                    Feedback = 0.65f;   // Moderate feedback for warmth without harshness
                    Mix = 0.7f;         // Wet-heavy mix for classic phaser prominence
                    Stages = 4;         // Classic 4-stage configuration
                    break;

                case PhaserPreset.Ambient:
                    // Subtle atmospheric phasing for background textures
                    // Very slow movement, gentle depth for unobtrusive ambience
                    Rate = 0.2f;        // 0.2 Hz - very slow, atmospheric movement
                    Depth = 0.4f;       // Shallow depth for subtlety
                    Feedback = 0.3f;    // Low feedback for gentle character
                    Mix = 0.3f;         // Dry-heavy mix to maintain original signal
                    Stages = 6;         // More stages for smoother, more complex sweeps
                    break;

                case PhaserPreset.Tremolo:
                    // Fast rhythmic phasing for pulsing effects
                    // High rate creates tremolo-like rhythmic pulsing
                    Rate = 4.0f;        // 4 Hz - fast rhythmic pulsing
                    Depth = 0.6f;       // Moderate depth for clear rhythmic effect
                    Feedback = 0.2f;    // Low feedback to avoid muddiness at high rates
                    Mix = 0.8f;         // Heavy wet signal for pronounced rhythmic effect
                    Stages = 3;         // Fewer stages for cleaner, faster response
                    break;

                case PhaserPreset.DeepSpace:
                    // Dramatic psychedelic phasing for experimental sounds
                    // Maximum depth and high feedback for otherworldly sweeps
                    Rate = 0.3f;        // 0.3 Hz - slow, dramatic sweeps
                    Depth = 1.0f;       // Maximum depth for extreme modulation
                    Feedback = 0.85f;   // High feedback for dramatic resonance
                    Mix = 0.9f;         // Almost entirely wet for maximum effect
                    Stages = 8;         // Maximum stages for complex, evolving sweeps
                    break;

                case PhaserPreset.GuitarSolo:
                    // Classic lead guitar phasing that cuts through the mix
                    // Medium-fast rate with good presence and clarity
                    Rate = 1.2f;        // 1.2 Hz - medium-fast sweep for energy
                    Depth = 0.75f;      // Good depth for character without muddiness
                    Feedback = 0.55f;   // Moderate feedback for presence
                    Mix = 0.6f;         // Balanced mix to maintain note definition
                    Stages = 4;         // Classic 4-stage for familiar guitar sound
                    break;

                case PhaserPreset.Vocal:
                    // Gentle vocal enhancement without distraction
                    // Slow, subtle movement to add character to vocal tracks
                    Rate = 0.4f;        // 0.4 Hz - gentle, slow movement
                    Depth = 0.5f;       // Moderate depth for character
                    Feedback = 0.35f;   // Low feedback to avoid vocal muddiness
                    Mix = 0.25f;        // Light mix to enhance without overpowering
                    Stages = 6;         // More stages for smoother, more musical sweep
                    break;

                case PhaserPreset.SynthPad:
                    // Lush modulation for synthesizer pads and strings
                    // Medium rate with deep modulation for evolving textures
                    Rate = 0.8f;        // 0.8 Hz - medium rate for evolving movement
                    Depth = 0.85f;      // Deep modulation for lush textures
                    Feedback = 0.75f;   // High feedback for rich harmonics
                    Mix = 0.8f;         // Wet-heavy for lush, processed sound
                    Stages = 6;         // More stages for complex, evolving character
                    break;
            }
        }

        /// <summary>
        /// Process samples with phaser effect.
        /// </summary>
        /// <param name="samples">Input samples</param>
        public override void Process(Span<float> samples)
        {
            float lfoIncrement = (float)(2.0 * Math.PI * Rate / _sampleRate);

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                float lfoValue = (float)Math.Sin(_lfoPhase);

                float minFreq = 200.0f;  // Hz
                float maxFreq = 2000.0f; // Hz
                float frequency = minFreq + (maxFreq - minFreq) * (0.5f + 0.5f * lfoValue * Depth);
                float coefficient = CalculateAllPassCoefficient(frequency);

                float processed = input;
                for (int stage = 0; stage < Stages; stage++)
                {
                    processed = _allPassFilters[stage].Process(processed, coefficient);
                }

                processed += input * Feedback;

                samples[i] = (input * (1.0f - Mix)) + (processed * Mix);

                _lfoPhase += lfoIncrement;

                if (_lfoPhase >= 2.0 * Math.PI)
                    _lfoPhase -= (float)(2.0 * Math.PI);
            }
        }

        /// <summary>
        /// Reset phaser effect state.
        /// </summary>
        public override void Reset()
        {
            _lfoPhase = 0.0f;
            foreach (var filter in _allPassFilters)
            {
                filter.Reset();
            }
        }

        /// <summary>
        /// Calculate all-pass filter coefficient from frequency
        /// </summary>
        private float CalculateAllPassCoefficient(float frequency)
        {
            float omega = (float)(2.0 * Math.PI * frequency / _sampleRate);
            float tanHalfOmega = (float)Math.Tan(omega * 0.5);
            return (tanHalfOmega - 1.0f) / (tanHalfOmega + 1.0f);
        }

        /// <summary>
        /// All-pass filter implementation
        /// </summary>
        private class AllPassFilter
        {
            private float _x1 = 0.0f;
            private float _y1 = 0.0f;

            public float Process(float input, float coefficient)
            {
                float output = -coefficient * input + _x1 + coefficient * _y1;
                _x1 = input;
                _y1 = output;
                return output;
            }

            public void Reset()
            {
                _x1 = 0.0f;
                _y1 = 0.0f;
            }
        }
    }
}
