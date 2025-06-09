using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Professional delay fx with improved audio quality and stability
    /// </summary>
    public class Delay : SampleProcessorBase
    {
        private float[]? _delayBuffer;
        private int _writeIndex;
        private int _readIndex;
        private readonly int _sampleRate;
        private int _delaySamples;
        private int _time;

        // High-frequency damping for more natural sound
        private float _dampingCoeff = 0.2f;
        private float _lastOutput = 0.0f;

        /// <summary>
        /// The delay time is in milliseconds.
        /// Automatically updates buffer length when modified.
        /// </summary>
        public int Time
        {
            get => _time;
            set
            {
                if (value <= 0)
                    throw new ArgumentException("The delay time must be positive.", nameof(Time));

                _time = value;
                UpdateDelayTime();
            }
        }

        /// <summary>
        /// The amount of feedback (value between 0.0 and 1.0).
        /// </summary>
        public float Repeat { get; set; }

        /// <summary>
        /// The mixing ratio of the original and delayed signal (value between 0.0 and 1.0).
        /// </summary>
        public float Mix { get; set; }

        /// <summary>
        /// Initialize Delay Processor.
        /// </summary>
        /// <param name="time">The delay time is in milliseconds.</param>
        /// <param name="repeat">The feedback rate (0.0 - 1.0).</param>
        /// <param name="mix">The mixing ratio of the original and delayed signal (0.0 - 1.0).</param>
        /// <param name="sampleRate">The sampling frequency (Hz).</param>
        public Delay(int time, float repeat, float mix, int sampleRate)
        {
            if (time <= 0)
                throw new ArgumentException("The delay time must be positive.", nameof(time));

            if (sampleRate <= 0)
                throw new ArgumentException("The sampling frequency must be positive.", nameof(sampleRate));

            if (repeat < 0.0f || repeat > 1.0f)
                throw new ArgumentException("The value of Repeat must be between 0.0 and 1.0.", nameof(repeat));

            if (mix < 0.0f || mix > 1.0f)
                throw new ArgumentException("The Mix value must be between 0.0 and 1.0.", nameof(mix));

            _sampleRate = sampleRate;
            Time = time;
            Repeat = repeat;
            Mix = mix;

            InitializeBuffer();
        }

        /// <summary>
        /// Sample processing with delay.
        /// </summary>
        /// <param name="samples">The input samples</param>
        public override void Process(Span<float> samples)
        {
#nullable disable
            for (int i = 0; i < samples.Length; i++)
            {
                float delayedSample = _delayBuffer[_readIndex];

                delayedSample = _lastOutput + _dampingCoeff * (delayedSample - _lastOutput);
                _lastOutput = delayedSample;

                float feedbackSample = SoftClip(samples[i] + (delayedSample * Repeat));

                _delayBuffer[_writeIndex] = feedbackSample;

                samples[i] = (samples[i] * (1.0f - Mix)) + (delayedSample * Mix);

                _writeIndex = (_writeIndex + 1) % _delaySamples;
                _readIndex = (_readIndex + 1) % _delaySamples;
            }
#nullable restore
        }

        /// <summary>
        /// Resets the delay effect by clearing the delay buffer and resetting the buffer index.
        /// Does not modify any settings or parameters.
        /// </summary>
        public override void Reset()
        {
            if (_delayBuffer != null)
            {
                Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            }
            _writeIndex = 0;
            _readIndex = 0;
            _lastOutput = 0.0f;
        }

        /// <summary>
        /// Update the delay time.
        /// </summary>
        private void UpdateDelayTime()
        {
            _delaySamples = Math.Max(1, (int)((Time / 1000.0) * _sampleRate));
            InitializeBuffer();
        }

        /// <summary>
        /// Initialize or reinitialize the delay buffer
        /// </summary>
        private void InitializeBuffer()
        {
            int bufferSize = Math.Max(_delaySamples, 64);

            if (_delayBuffer == null || _delayBuffer.Length != bufferSize)
            {
                _delayBuffer = new float[bufferSize];
            }
            else
            {
                Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            }

            _writeIndex = 0;
            _readIndex = 0;
            _lastOutput = 0.0f;
        }

        /// <summary>
        /// Soft clipping function to prevent harsh distortion
        /// </summary>
        /// <param name="input">Input sample</param>
        /// <returns>Soft-clipped sample</returns>
        private static float SoftClip(float input)
        {
            const float threshold = 0.7f;

            if (Math.Abs(input) <= threshold)
                return input;

            float sign = Math.Sign(input);
            float abs = Math.Abs(input);

            // Smooth saturation curve
            return sign * (threshold + (1.0f - threshold) * (1.0f - 1.0f / (1.0f + (abs - threshold) * 2.0f)));
        }
    }
}
