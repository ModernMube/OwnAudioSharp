using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Phaser effect with all-pass filter stages
    /// </summary>
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
        /// Initialize Phaser Processor.
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
