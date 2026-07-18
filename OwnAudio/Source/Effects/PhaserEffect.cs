using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Phaser setups from gentle shimmer to full psychedelic sweep.
    /// </summary>
    public enum PhaserPreset
    {
        /// <summary>
        /// Balanced starting point.
        /// </summary>
        Default,

        /// <summary>
        /// Phase 90 flavour, warm 70s sweep.
        /// </summary>
        Vintage,

        /// <summary>
        /// Slow and shallow, background movement only.
        /// </summary>
        Ambient,

        /// <summary>
        /// Fast pulse, phaser used as a tremolo.
        /// </summary>
        Tremolo,

        /// <summary>
        /// Slow, maximum depth and resonance.
        /// </summary>
        DeepSpace,

        /// <summary>
        /// Cutting lead sound.
        /// </summary>
        GuitarSolo,

        /// <summary>
        /// Light colouring, doesn't bury the voice.
        /// </summary>
        Vocal,

        /// <summary>
        /// Lush evolving pad modulation.
        /// </summary>
        SynthPad
    }

    /// <summary>
    /// Phaser: a chain of all-pass stages swept by an LFO, mixed back with the dry signal.
    /// </summary>
    public sealed class PhaserEffect : IEffectProcessor
    {
        private readonly int _sampleRate;

        /// <summary>
        /// All-pass state, x[n-1] and y[n-1] for the 8 possible stages.
        /// </summary>
        private readonly float[] _apX1 = new float[8];
        private readonly float[] _apY1 = new float[8];

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
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// LFO speed in Hz, 0.1 - 10.
        /// </summary>
        public float Rate
        {
            get => _rate;
            set => _rate = Math.Clamp(value, 0.1f, 10.0f);
        }

        /// <summary>
        /// Sweep depth, 0 - 1.
        /// </summary>
        public float Depth
        {
            get => _depth;
            set => _depth = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Feedback, capped at 0.95.
        /// </summary>
        public float Feedback
        {
            get => _feedback;
            set => _feedback = Math.Clamp(value, 0.0f, 0.95f);
        }

        /// <summary>
        /// Dry to wet balance.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// How many all-pass stages are in the chain, 2 - 8.
        /// </summary>
        public int Stages
        {
            get => _stages;
            set => _stages = Math.Clamp(value, 2, 8);
        }

        /// <summary>
        /// Sample rate this instance was built for.
        /// </summary>
        public int SampleRate => _sampleRate;

        /// <summary>
        /// Builds the phaser with hand picked values.
        /// </summary>
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
        }

        /// <summary>
        /// Builds the phaser from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public PhaserEffect(PhaserPreset preset, int sampleRate = 44100)
        {
            _id = Guid.NewGuid();
            _name = "Phaser";
            _enabled = true;

            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;
            SetPreset(preset);
        }

        /// <summary>
        /// Stores the engine config.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(PhaserPreset preset)
        {
            switch (preset)
            {
                case PhaserPreset.Vintage:
                    Rate = 0.6f; Depth = 0.75f; Feedback = 0.62f; Mix = 0.60f; Stages = 4;
                    break;

                case PhaserPreset.Ambient:
                    Rate = 0.2f; Depth = 0.40f; Feedback = 0.28f; Mix = 0.28f; Stages = 6;
                    break;

                case PhaserPreset.Tremolo:
                    Rate = 4.0f; Depth = 0.58f; Feedback = 0.18f; Mix = 0.70f; Stages = 3;
                    break;

                case PhaserPreset.DeepSpace:
                    Rate = 0.3f; Depth = 1.0f; Feedback = 0.85f; Mix = 0.82f; Stages = 8;
                    break;

                case PhaserPreset.GuitarSolo:
                    Rate = 1.2f; Depth = 0.72f; Feedback = 0.52f; Mix = 0.55f; Stages = 4;
                    break;

                case PhaserPreset.Vocal:
                    Rate = 0.4f; Depth = 0.45f; Feedback = 0.30f; Mix = 0.22f; Stages = 6;
                    break;

                case PhaserPreset.SynthPad:
                    Rate = 0.8f; Depth = 0.80f; Feedback = 0.65f; Mix = 0.68f; Stages = 6;
                    break;

                default:
                    Rate = 0.5f; Depth = 0.65f; Feedback = 0.45f; Mix = 0.45f; Stages = 4;
                    break;
            }
        }

        /// <summary>
        /// Sweeps the notch between 200Hz and 2kHz and runs the chain per sample.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled) return;

            int sampleCount = frameCount * _config.Channels;
            int stages = _stages;
            float depth = _depth;
            float fb = _feedback;
            float mx = _mix;

            float lfoIncrement = (float)(2.0 * Math.PI * _rate / _sampleRate);

            for (int i = 0; i < sampleCount; i++)
            {
                float input = buffer[i];

                float lfo = MathF.Sin(_lfoPhase);
                float freq = 200.0f + 1800.0f * (0.5f + 0.5f * lfo * depth);
                float coeff = _allPassCoeff(freq);

                float processed = input;
                for (int s = 0; s < stages; s++)
                {
                    float output = -coeff * processed + _apX1[s] + coeff * _apY1[s];
                    _apX1[s] = processed;
                    _apY1[s] = output;
                    processed = output;
                }

                processed += input * fb;
                buffer[i] = input * (1.0f - mx) + processed * mx;

                _lfoPhase += lfoIncrement;
                if (_lfoPhase >= 2.0 * Math.PI) _lfoPhase -= (float)(2.0 * Math.PI);
            }
        }

        /// <summary>
        /// Clears every stage and parks the LFO.
        /// </summary>
        public void Reset()
        {
            _lfoPhase = 0.0f;
            Array.Clear(_apX1, 0, _apX1.Length);
            Array.Clear(_apY1, 0, _apY1.Length);
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
            return $"Phaser [ID: {_id}, Enabled: {_enabled}, Rate: {_rate:F2}Hz, Depth: {_depth:F2}, Stages: {_stages}]";
        }

        /// <summary>
        /// Bilinear all-pass coefficient for the given corner frequency.
        /// </summary>
        private float _allPassCoeff(float frequency)
        {
            float omega = (float)(2.0 * Math.PI * frequency / _sampleRate);
            float t = MathF.Tan(omega * 0.5f);
            return (t - 1.0f) / (t + 1.0f);
        }
    }
}
