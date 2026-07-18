using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Flanger setups from subtle thickening to jet sweep.
    /// </summary>
    public enum FlangerPreset
    {
        /// <summary>
        /// All purpose, audible but not in your face.
        /// </summary>
        Default,

        /// <summary>
        /// Tape style swoosh.
        /// </summary>
        Classic,

        /// <summary>
        /// Fast and deep with lots of feedback.
        /// </summary>
        JetPlane,

        /// <summary>
        /// Slow and shallow, more like a chorus.
        /// </summary>
        SubtleChorus,

        /// <summary>
        /// Natural doubling for vocals.
        /// </summary>
        VocalDoubling,

        /// <summary>
        /// Cutting lead sound for solos.
        /// </summary>
        GuitarLead,

        /// <summary>
        /// Very slow, deep and dreamy.
        /// </summary>
        AmbientWash,

        /// <summary>
        /// Fast and tight, keeps the drum attack.
        /// </summary>
        Percussive
    }

    /// <summary>
    /// Flanger: short LFO swept delay fed back into itself.
    /// </summary>
    public sealed class FlangerEffect : IEffectProcessor
    {
        /// <summary>
        /// 20ms delay line, sized once at construction.
        /// </summary>
        private readonly float[] _delayBuffer;
        private readonly int _sampleRate;
        private int _bufferIndex;

        private float _rate = 0.5f;
        private float _depth = 0.8f;
        private float _feedback = 0.6f;
        private float _mix = 0.5f;
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
        /// LFO speed in Hz, 0.1 - 5.
        /// </summary>
        public float Rate
        {
            get => _rate;
            set => _rate = Math.Clamp(value, 0.1f, 5.0f);
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
        /// Feedback, capped at 0.95 so it doesn't run away.
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
        /// Sample rate this instance was built for.
        /// </summary>
        public int SampleRate => _sampleRate;

        /// <summary>
        /// Builds the flanger with hand picked values.
        /// </summary>
        public FlangerEffect(float rate = 0.5f, float depth = 0.8f, float feedback = 0.6f, float mix = 0.5f, int sampleRate = 44100)
        {
            _id = Guid.NewGuid();
            _name = "Flanger";
            _enabled = true;

            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;
            Rate = rate;
            Depth = depth;
            Feedback = feedback;
            Mix = mix;

            _delayBuffer = new float[(int)(0.02 * sampleRate)];
        }

        /// <summary>
        /// Builds the flanger from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public FlangerEffect(FlangerPreset preset, int sampleRate = 44100)
        {
            _id = Guid.NewGuid();
            _name = "Flanger";
            _enabled = true;

            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;
            _delayBuffer = new float[(int)(0.02 * sampleRate)];

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
        public void SetPreset(FlangerPreset preset)
        {
            switch (preset)
            {
                case FlangerPreset.Default:
                    Rate = 0.5f; Depth = 0.65f; Feedback = 0.55f; Mix = 0.48f;
                    break;

                case FlangerPreset.Classic:
                    Rate = 0.7f; Depth = 0.75f; Feedback = 0.65f; Mix = 0.45f;
                    break;

                case FlangerPreset.JetPlane:
                    Rate = 2.8f; Depth = 0.95f; Feedback = 0.85f; Mix = 0.65f;
                    break;

                case FlangerPreset.SubtleChorus:
                    Rate = 0.25f; Depth = 0.35f; Feedback = 0.25f; Mix = 0.30f;
                    break;

                case FlangerPreset.VocalDoubling:
                    Rate = 0.4f; Depth = 0.55f; Feedback = 0.35f; Mix = 0.35f;
                    break;

                case FlangerPreset.GuitarLead:
                    Rate = 1.5f; Depth = 0.85f; Feedback = 0.75f; Mix = 0.55f;
                    break;

                case FlangerPreset.AmbientWash:
                    Rate = 0.15f; Depth = 0.90f; Feedback = 0.55f; Mix = 0.60f;
                    break;

                case FlangerPreset.Percussive:
                    Rate = 3.5f; Depth = 0.60f; Feedback = 0.40f; Mix = 0.40f;
                    break;
            }
        }

        /// <summary>
        /// Sweeps the read tap with the LFO and blends the delayed signal back in.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled) return;

            int sampleCount = frameCount * _config.Channels;
            int bufLen = _delayBuffer.Length;

            float lfoIncrement = (float)(2.0 * Math.PI * _rate / _sampleRate);
            float depth = _depth;
            float fb = _feedback;
            float mx = _mix;

            for (int i = 0; i < sampleCount; i++)
            {
                float input = buffer[i];

                float lfo = MathF.Sin(_lfoPhase);
                float delayTime = 0.001f + 0.009f * (1.0f + lfo * depth) * 0.5f;
                int delaySamples = Math.Clamp((int)(delayTime * _sampleRate), 1, bufLen - 1);

                int readIndex = (_bufferIndex - delaySamples + bufLen) % bufLen;
                float delayed = _delayBuffer[readIndex];

                _delayBuffer[_bufferIndex] = Math.Clamp(input + delayed * fb, -1.0f, 1.0f);
                buffer[i] = input * (1.0f - mx) + delayed * mx;

                _bufferIndex++;
                if (_bufferIndex >= bufLen) _bufferIndex = 0;

                _lfoPhase += lfoIncrement;
                if (_lfoPhase >= 2.0 * Math.PI) _lfoPhase -= (float)(2.0 * Math.PI);
            }
        }

        /// <summary>
        /// Empties the delay line and parks the LFO.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _bufferIndex = 0;
            _lfoPhase = 0.0f;
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
            return $"Flanger [ID: {_id}, Enabled: {_enabled}, Rate: {_rate:F2}Hz, Depth: {_depth:F2}, Mix: {_mix:F2}]";
        }
    }
}
