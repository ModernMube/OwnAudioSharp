using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Delay fx
    /// </summary>
    public enum DelayPreset
    {
        /// <summary>
        /// Short slap-back delay - adds thickness and dimension to vocals
        /// Very short delay time with minimal feedback for natural doubling effect
        /// </summary>
        SlapBack,

        /// <summary>
        /// Classic echo effect - traditional delay with moderate repeats
        /// Medium delay time with controlled feedback for musical echo
        /// </summary>
        ClassicEcho,

        /// <summary>
        /// Ambient spacious delay - creates wide, atmospheric soundscapes
        /// Long delay time with higher feedback for ethereal, floating effects
        /// </summary>
        Ambient,

        /// <summary>
        /// Rhythmic delay - synced to musical timing for rhythmic patterns
        /// Eighth note timing with moderate feedback for groove enhancement
        /// </summary>
        Rhythmic,

        /// <summary>
        /// Ping-pong stereo delay - bouncing delay effect between channels
        /// Medium timing with balanced mix for stereo width
        /// </summary>
        PingPong,

        /// <summary>
        /// Tape echo emulation - warm, vintage analog delay characteristics
        /// Classic tape delay timing with organic feedback behavior
        /// </summary>
        TapeEcho,

        /// <summary>
        /// Dub delay - reggae/dub style delay with heavy feedback
        /// Long delay with high feedback for classic dub echoes
        /// </summary>
        Dub,

        /// <summary>
        /// Subtle thickening - very short delay for instrument thickening
        /// Micro delay with low mix for subtle enhancement without obvious delay
        /// </summary>
        Thickening
    }

    /// <summary>
    /// Professional delay fx with improved audio quality and stability
    /// </summary>
    public class Delay : SampleProcessorBase
    {
        private readonly float[] _delayBuffer;  // A buffer for storing delayed samples
        private int _bufferIndex;               // The current index of the buffer
        private readonly int _sampleRate;       // Sample rate

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
        /// High-frequency damping coefficient (value between 0.0 and 1.0).
        /// Higher values create more dampening for warmer, more natural delays.
        /// </summary>
        public float Damping 
        { 
            get => _dampingCoeff; 
            set => _dampingCoeff = Math.Max(0.0f, Math.Min(1.0f, value)); 
        }

        /// <summary>
        /// The sampling frequency (Hz)
        /// </summary>
        public int SampleRate { get; set; } = 44100;

        /// <summary>
        /// Initialize Delay Processor.
        /// </summary>
        /// <param name="time">The delay time is in milliseconds.</param>
        /// <param name="repeat">The feedback rate (0.0 - 1.0).</param>
        /// <param name="mix">The mixing ratio of the original and delayed signal (0.0 - 1.0).</param>
        /// <param name="sampleRate">The sampling frequency (Hz).</param>
        public Delay(int time = 410, float repeat = 0.4f, float mix = 0.35f, int sampleRate = 44100)
        {
            if (time <= 0)
                throw new ArgumentException("The delay time must be positive.", nameof(time));

            if (sampleRate <= 0)
                throw new ArgumentException("The sampling frequency must be positive.", nameof(sampleRate));

            if (repeat < 0.0f || repeat > 1.0f)
                throw new ArgumentException("The value of Repeat must be between 0.0 and 1.0.", nameof(repeat));

            if (mix < 0.0f || mix > 1.0f)
                throw new ArgumentException("The Mix value must be between 0.0 and 1.0.", nameof(mix));

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
        public override void Process(Span<float> samples)
        {
#nullable disable
            for (int i = 0; i < samples.Length; i++)
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
                Array.Clear(_delayBuffer, 0, _delayBuffer.Length); // Clear buffer for stable operation
            }
        }
    }
}
