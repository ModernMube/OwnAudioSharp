using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Rotary speaker simulation with horn and rotor
    /// </summary>
    public class Rotary : SampleProcessorBase
    {
        private readonly int _sampleRate;
        private readonly float[] _hornDelayBuffer;
        private readonly float[] _rotorDelayBuffer;

        private float _hornSpeed = 6.0f;
        private float _rotorSpeed = 1.0f;
        private float _intensity = 0.7f;
        private float _mix = 1.0f;
        private bool _isFast = false;

        private float _hornPhase = 0.0f;
        private float _rotorPhase = 0.0f;
        private int _hornBufferIndex = 0;
        private int _rotorBufferIndex = 0;

        // Crossover filters
        private LowPassFilter _lowPassFilter;
        private HighPassFilter _highPassFilter;

        /// <summary>
        /// Horn rotation speed in Hz (2.0 - 15.0).
        /// </summary>
        public float HornSpeed
        {
            get => _hornSpeed;
            set => _hornSpeed = Math.Clamp(value, 2.0f, 15.0f);
        }

        /// <summary>
        /// Rotor rotation speed in Hz (0.5 - 5.0).
        /// </summary>
        public float RotorSpeed
        {
            get => _rotorSpeed;
            set => _rotorSpeed = Math.Clamp(value, 0.5f, 5.0f);
        }

        /// <summary>
        /// Effect intensity (0.0 - 1.0).
        /// </summary>
        public float Intensity
        {
            get => _intensity;
            set => _intensity = Math.Clamp(value, 0.0f, 1.0f);
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
        /// Fast/slow speed switch.
        /// </summary>
        public bool IsFast
        {
            get => _isFast;
            set => _isFast = value;
        }

        /// <summary>
        /// Initialize Rotary Processor.
        /// </summary>
        /// <param name="hornSpeed">Horn speed in Hz (2.0 - 15.0)</param>
        /// <param name="rotorSpeed">Rotor speed in Hz (0.5 - 5.0)</param>
        /// <param name="intensity">Effect intensity (0.0 - 1.0)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="sampleRate">Sample rate</param>
        public Rotary(float hornSpeed = 6.0f, float rotorSpeed = 1.0f, float intensity = 0.7f, float mix = 1.0f, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;
            HornSpeed = hornSpeed;
            RotorSpeed = rotorSpeed;
            Intensity = intensity;
            Mix = mix;

            int maxDelay = (int)(0.01 * sampleRate); // 10ms max delay
            _hornDelayBuffer = new float[maxDelay];
            _rotorDelayBuffer = new float[maxDelay];

            _lowPassFilter = new LowPassFilter(800.0f, sampleRate);
            _highPassFilter = new HighPassFilter(800.0f, sampleRate);
        }

        /// <summary>
        /// Process samples with rotary speaker effect.
        /// </summary>
        /// <param name="samples">Input samples</param>
        public override void Process(Span<float> samples)
        {
            float currentHornSpeed = _isFast ? HornSpeed * 3.0f : HornSpeed;
            float currentRotorSpeed = _isFast ? RotorSpeed * 2.0f : RotorSpeed;

            float hornIncrement = (float)(2.0 * Math.PI * currentHornSpeed / _sampleRate);
            float rotorIncrement = (float)(2.0 * Math.PI * currentRotorSpeed / _sampleRate);

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                float lowFreq = _lowPassFilter.Process(input);
                float highFreq = _highPassFilter.Process(input);

                float hornLfo = (float)Math.Sin(_hornPhase);
                float hornDelay = 0.001f + (0.003f * hornLfo * Intensity);
                int hornDelaySamples = (int)(hornDelay * _sampleRate);
                hornDelaySamples = Math.Clamp(hornDelaySamples, 1, _hornDelayBuffer.Length - 1);

                int hornReadIndex = (_hornBufferIndex - hornDelaySamples + _hornDelayBuffer.Length) % _hornDelayBuffer.Length;
                float hornOutput = _hornDelayBuffer[hornReadIndex];

                hornOutput *= (0.8f + 0.2f * hornLfo * Intensity);

                _hornDelayBuffer[_hornBufferIndex] = highFreq;

                float rotorLfo = (float)Math.Sin(_rotorPhase);
                float rotorDelay = 0.002f + (0.004f * rotorLfo * Intensity);
                int rotorDelaySamples = (int)(rotorDelay * _sampleRate);
                rotorDelaySamples = Math.Clamp(rotorDelaySamples, 1, _rotorDelayBuffer.Length - 1);

                int rotorReadIndex = (_rotorBufferIndex - rotorDelaySamples + _rotorDelayBuffer.Length) % _rotorDelayBuffer.Length;
                float rotorOutput = _rotorDelayBuffer[rotorReadIndex];

                rotorOutput *= (0.9f + 0.1f * rotorLfo * Intensity);

                _rotorDelayBuffer[_rotorBufferIndex] = lowFreq;

                float processed = hornOutput + rotorOutput;

                samples[i] = (input * (1.0f - Mix)) + (processed * Mix);

                _hornBufferIndex = (_hornBufferIndex + 1) % _hornDelayBuffer.Length;
                _rotorBufferIndex = (_rotorBufferIndex + 1) % _rotorDelayBuffer.Length;

                _hornPhase += hornIncrement;
                _rotorPhase += rotorIncrement;

                if (_hornPhase >= 2.0 * Math.PI)
                    _hornPhase -= (float)(2.0 * Math.PI);
                if (_rotorPhase >= 2.0 * Math.PI)
                    _rotorPhase -= (float)(2.0 * Math.PI);
            }
        }

        /// <summary>
        /// Reset rotary effect state.
        /// </summary>
        public override void Reset()
        {
            Array.Clear(_hornDelayBuffer, 0, _hornDelayBuffer.Length);
            Array.Clear(_rotorDelayBuffer, 0, _rotorDelayBuffer.Length);
            _hornBufferIndex = 0;
            _rotorBufferIndex = 0;
            _hornPhase = 0.0f;
            _rotorPhase = 0.0f;
            _lowPassFilter.Reset();
            _highPassFilter.Reset();
        }

        /// <summary>
        /// Simple low-pass filter for crossover
        /// </summary>
        private class LowPassFilter
        {
            private float _cutoff;
            private float _state;

            public LowPassFilter(float cutoffFreq, int sampleRate)
            {
                _cutoff = (float)(2.0 * Math.PI * cutoffFreq / sampleRate);
                _state = 0.0f;
            }

            public float Process(float input)
            {
                _state += _cutoff * (input - _state);
                return _state;
            }

            public void Reset()
            {
                _state = 0.0f;
            }
        }

        /// <summary>
        /// Simple high-pass filter for crossover
        /// </summary>
        private class HighPassFilter
        {
            private readonly LowPassFilter _lowPass;

            public HighPassFilter(float cutoffFreq, int sampleRate)
            {
                _lowPass = new LowPassFilter(cutoffFreq, sampleRate);
            }

            public float Process(float input)
            {
                return input - _lowPass.Process(input);
            }

            public void Reset()
            {
                _lowPass.Reset();
            }
        }
    }
}
