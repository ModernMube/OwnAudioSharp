using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Delay fx
    /// </summary>
    public class Delay : SampleProcessorBase
    {
        private readonly float[] _delayBuffer;  // A buffer for storing delayed samples
        private int _bufferIndex;               // The current index of the buffer
        private readonly int _sampleRate;       // Sample rate

        private int _delaySamples;              // The length of the delay in samples
        private int _time;                      // A Time backing field

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
                    throw new ArgumentException("A késleltetési időnek pozitívnak kell lennie.", nameof(Time));

                _time = value;
                UpdateDelayTime();  // Automatic update
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
                throw new ArgumentException("A késleltetésnek pozitívnak kell lennie.", nameof(time));

            if (sampleRate <= 0)
                throw new ArgumentException("A mintavételi frekvenciának pozitívnak kell lennie.", nameof(sampleRate));

            if (repeat < 0.0f || repeat > 1.0f)
                throw new ArgumentException("A Repeat értékének 0.0 és 1.0 között kell lennie.", nameof(repeat));

            if (mix < 0.0f || mix > 1.0f)
                throw new ArgumentException("A Mix értékének 0.0 és 1.0 között kell lennie.", nameof(mix));

            Time = time;
            Repeat = repeat;
            Mix = mix;
            _sampleRate = sampleRate;

            _delaySamples = (int)((time / 1000.0) * sampleRate); // Delay in samples
            _delayBuffer = new float[_delaySamples];
            _bufferIndex = 0;
        }

        /// <summary>
        /// Sample processing with delay.
        /// </summary>
        /// <param name="samples">The input samples</param>
        /// <returns>A kevert minta.</returns>
        public override void Process(Span<float> samples)
        {
            for(int i = 0; i < samples.Length; i++)
            {
                
                float delayedSample = _delayBuffer[_bufferIndex];       // Retrieve the delayed pattern
                _delayBuffer[_bufferIndex] = samples[i] + (delayedSample * Repeat);     // New sample in buffer with feedback                 
                _bufferIndex = (_bufferIndex + 1) % _delaySamples;      // Increasing the buffer index (circular operation)
                samples[i] = (samples[i] * (1.0f - Mix)) + (delayedSample * Mix);       // Mix original and delayed signal
            }
            
        }

        /// <summary>
        /// Update the delay time.
        /// </summary>
        private void UpdateDelayTime()
        {
            if (_delayBuffer is not null)
            {
                _delaySamples = (int)((Time / 1000.0) * _sampleRate);
                Array.Clear(_delayBuffer, 0, _delayBuffer.Length); // Puffer törlése a stabil működés érdekében
            }
        }
    }
}
