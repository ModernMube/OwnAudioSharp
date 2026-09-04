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

        private float _rate = 0.45f;
        private float _depth = 0.65f;
        private float _feedback = 0.40f;
        private float _mix = 0.45f;
        private int _stages = 4;

        private readonly Guid _id;
        private readonly string _name;
        private bool _enabled;
        private bool _disposed;
        private readonly NativeEffectEngine _native = new NativeEffectEngine();
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
        /// Dry to wet balance. The notches come from cancelling against the dry, so they are
        /// deepest at 0.5 — the presets stay at or under it.
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
        /// Builds the phaser with hand picked values, the same four stage sweep as the
        /// Default preset.
        /// </summary>
        public PhaserEffect(float rate = 0.45f, float depth = 0.65f, float feedback = 0.40f, float mix = 0.45f, int stages = 4, int sampleRate = 44100)
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
            _native.Initialize(this, config);
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
                    Rate = 0.55f; Depth = 0.75f; Feedback = 0.35f; Mix = 0.50f; Stages = 4;
                    break;

                case PhaserPreset.Ambient:
                    Rate = 0.15f; Depth = 0.45f; Feedback = 0.25f; Mix = 0.35f; Stages = 6;
                    break;

                case PhaserPreset.Tremolo:
                    Rate = 3.50f; Depth = 0.60f; Feedback = 0.15f; Mix = 0.50f; Stages = 2;
                    break;

                case PhaserPreset.DeepSpace:
                    Rate = 0.25f; Depth = 1.0f; Feedback = 0.85f; Mix = 0.50f; Stages = 8;
                    break;

                case PhaserPreset.GuitarSolo:
                    Rate = 1.00f; Depth = 0.72f; Feedback = 0.50f; Mix = 0.50f; Stages = 4;
                    break;

                case PhaserPreset.Vocal:
                    Rate = 0.35f; Depth = 0.40f; Feedback = 0.20f; Mix = 0.25f; Stages = 6;
                    break;

                case PhaserPreset.SynthPad:
                    Rate = 0.70f; Depth = 0.80f; Feedback = 0.60f; Mix = 0.45f; Stages = 6;
                    break;

                default:
                    Rate = 0.45f; Depth = 0.65f; Feedback = 0.40f; Mix = 0.45f; Stages = 4;
                    break;
            }
        }

        /// <summary>
        /// Same DSP the mixer twin runs, on this instance's native handle.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount)
        {
            _native.Process(this, buffer, frameCount);
        }

        /// <summary>
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        /// <summary>
        /// Clears every stage and parks the LFO.
        /// </summary>
        public void Reset()
        {
            ResetGeneration++;
            _native.Reset();
        }

        /// <summary>
        /// Nothing unmanaged here.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            Reset();
            _native.Dispose();
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
        /// Bilinear all-pass coefficient for the given corner frequency. Comes out negative
        /// for anything well under Nyquist, and the sign has to survive into the filter.
        /// </summary>
        private float _allPassCoeff(float frequency)
        {
            float omega = (float)(2.0 * Math.PI * frequency / _sampleRate);
            float t = MathF.Tan(omega * 0.5f);
            return (t - 1.0f) / (t + 1.0f);
        }
    }
}
