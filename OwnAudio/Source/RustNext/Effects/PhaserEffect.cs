using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.RustNext.Effects
{
    /// <summary>
    /// Phaser presets for different audio processing scenarios
    /// </summary>
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
    public sealed class PhaserEffect : IEffectProcessor
    {
        private readonly int _sampleRate;
        private readonly AllPassFilter[] _allPassFilters;

        private float _rate = 0.5f;
        private float _depth = 0.7f;
        private float _feedback = 0.5f;
        private float _mix = 0.5f;
        private int _stages = 4;

        private float _lfoPhase = 0.0f;

        private readonly Guid _id;
        private readonly string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig? _config;

        /// <summary>
        /// Gets the unique identifier for this effect instance
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Gets the name of this effect
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Gets or sets whether this effect is enabled
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

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
        /// Gets the sample rate in Hz (set at construction time).
        /// </summary>
        public int SampleRate => _sampleRate;

        /// <summary>
        /// Initialize Phaser Processor with all parameters.
        /// </summary>
        /// <param name="rate">LFO rate in Hz (0.1 - 10.0)</param>
        /// <param name="depth">Modulation depth (0.0 - 1.0)</param>
        /// <param name="feedback">Feedback amount (0.0 - 0.95)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="stages">Number of stages (2 - 8)</param>
        /// <param name="sampleRate">Sample rate</param>
        public PhaserEffect(float rate = 0.5f, float depth = 0.7f, float feedback = 0.5f, float mix = 0.5f, int stages = 4, int sampleRate = 44100)
        {
            _id = Guid.NewGuid();
            _name = "Phaser";
            _enabled = true;

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
        public PhaserEffect(PhaserPreset preset, int sampleRate = 44100)
        {
            _id = Guid.NewGuid();
            _name = "Phaser";
            _enabled = true;

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
        /// Initializes the effect with the given audio configuration
        /// </summary>
        /// <param name="config">Audio configuration</param>
        public void Initialize(AudioConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Set phaser parameters using predefined presets
        /// </summary>
        public void SetPreset(PhaserPreset preset)
        {
            switch (preset)
            {
                case PhaserPreset.Default:
                    Rate = 0.5f; Depth = 0.65f; Feedback = 0.45f; Mix = 0.45f; Stages = 4;
                    break;

                case PhaserPreset.Vintage:
                    // MXR Phase 90 style – warm, musical 70s character
                    Rate = 0.6f; Depth = 0.75f; Feedback = 0.62f; Mix = 0.60f; Stages = 4;
                    break;

                case PhaserPreset.Ambient:
                    // Slow evolving texture – barely perceptible movement
                    Rate = 0.2f; Depth = 0.40f; Feedback = 0.28f; Mix = 0.28f; Stages = 6;
                    break;

                case PhaserPreset.Tremolo:
                    // Fast rhythmic pulse – phaser as tremolo substitute
                    Rate = 4.0f; Depth = 0.58f; Feedback = 0.18f; Mix = 0.70f; Stages = 3;
                    break;

                case PhaserPreset.DeepSpace:
                    // Slow, extreme psychedelic sweep – maximum resonance
                    Rate = 0.3f; Depth = 1.0f; Feedback = 0.85f; Mix = 0.82f; Stages = 8;
                    break;

                case PhaserPreset.GuitarSolo:
                    // Medium-fast cutting phaser for lead – EVH/script logo style
                    Rate = 1.2f; Depth = 0.72f; Feedback = 0.52f; Mix = 0.55f; Stages = 4;
                    break;

                case PhaserPreset.Vocal:
                    // Very gentle vocal coloring – adds shimmer without drowning the voice
                    Rate = 0.4f; Depth = 0.45f; Feedback = 0.30f; Mix = 0.22f; Stages = 6;
                    break;

                case PhaserPreset.SynthPad:
                    // Rich evolving pad modulation – lush but controlled
                    Rate = 0.8f; Depth = 0.80f; Feedback = 0.65f; Mix = 0.68f; Stages = 6;
                    break;
            }
        }

        /// <summary>
        /// Process samples with phaser effect.
        /// </summary>
        /// <param name="buffer">Input samples</param>
        /// <param name="frameCount">Number of frames to process</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled)
                return;

            int sampleCount = frameCount * _config.Channels;

            float lfoIncrement = (float)(2.0 * Math.PI * Rate / _sampleRate);

            for (int i = 0; i < sampleCount; i++)
            {
                float input = buffer[i];

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

                buffer[i] = (input * (1.0f - Mix)) + (processed * Mix);

                _lfoPhase += lfoIncrement;

                if (_lfoPhase >= 2.0 * Math.PI)
                    _lfoPhase -= (float)(2.0 * Math.PI);
            }
        }

        /// <summary>
        /// Reset phaser effect state.
        /// </summary>
        public void Reset()
        {
            _lfoPhase = 0.0f;
            foreach (var filter in _allPassFilters)
            {
                filter.Reset();
            }
        }

        /// <summary>
        /// Disposes of the effect and releases any resources
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            Reset();
            _disposed = true;
        }

        /// <summary>
        /// Returns a string representation of this effect
        /// </summary>
        public override string ToString()
        {
            return $"Phaser [ID: {_id}, Enabled: {_enabled}, Rate: {_rate:F2}Hz, Depth: {_depth:F2}, Stages: {_stages}]";
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
