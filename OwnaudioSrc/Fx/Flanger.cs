using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Flanger effect with variable delay modulation
    /// </summary>
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
        /// Initialize Flanger Processor.
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
