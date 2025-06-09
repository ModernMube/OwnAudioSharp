using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Chorus effect with multiple delayed voices
    /// </summary>
    public class Chorus : SampleProcessorBase
    {
        private readonly float[] _delayBuffer;
        private readonly int _sampleRate;
        private int _bufferIndex;

        private float _rate = 1.0f;
        private float _depth = 0.5f;
        private float _mix = 0.5f;
        private int _voices = 3;

        private float _lfoPhase = 0.0f;
        private readonly float[] _voicePhases;

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
        /// Mix between dry and wet signal (0.0 - 1.0).
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Number of chorus voices (2 - 6).
        /// </summary>
        public int Voices
        {
            get => _voices;
            set => _voices = Math.Clamp(value, 2, 6);
        }

        /// <summary>
        /// Initialize Chorus Processor.
        /// </summary>
        /// <param name="rate">LFO rate in Hz (0.1 - 10.0)</param>
        /// <param name="depth">Modulation depth (0.0 - 1.0)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="voices">Number of voices (2 - 6)</param>
        /// <param name="sampleRate">Sample rate</param>
        public Chorus(float rate = 1.0f, float depth = 0.5f, float mix = 0.5f, int voices = 3, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;
            Rate = rate;
            Depth = depth;
            Mix = mix;
            Voices = voices;

            int maxDelaySamples = (int)(0.05 * sampleRate);
            _delayBuffer = new float[maxDelaySamples];
            _bufferIndex = 0;

            _voicePhases = new float[6]; // Max voices
            for (int i = 0; i < _voicePhases.Length; i++)
            {
                _voicePhases[i] = (float)(i * 2.0 * Math.PI / 6.0);
            }
        }

        /// <summary>
        /// Process samples with chorus effect.
        /// </summary>
        /// <param name="samples">Input samples</param>
        public override void Process(Span<float> samples)
        {
            float lfoIncrement = (float)(2.0 * Math.PI * Rate / _sampleRate);

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                _delayBuffer[_bufferIndex] = input;

                float chorusOutput = 0.0f;

                for (int voice = 0; voice < Voices; voice++)
                {
                    float lfoValue = (float)Math.Sin(_lfoPhase + _voicePhases[voice]);

                    float delayTime = 0.01f + (0.015f * (1.0f + lfoValue * Depth));
                    int delaySamples = (int)(delayTime * _sampleRate);

                    int readIndex = (_bufferIndex - delaySamples + _delayBuffer.Length) % _delayBuffer.Length;
                    float delayedSample = _delayBuffer[readIndex];

                    chorusOutput += delayedSample;
                }

                chorusOutput /= Voices;

                samples[i] = (input * (1.0f - Mix)) + (chorusOutput * Mix);

                _bufferIndex = (_bufferIndex + 1) % _delayBuffer.Length;
                _lfoPhase += lfoIncrement;

                if (_lfoPhase >= 2.0 * Math.PI)
                    _lfoPhase -= (float)(2.0 * Math.PI);
            }
        }

        /// <summary>
        /// Reset chorus effect state.
        /// </summary>
        public override void Reset()
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _bufferIndex = 0;
            _lfoPhase = 0.0f;
        }
    }
}
