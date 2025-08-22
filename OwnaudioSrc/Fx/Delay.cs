using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Delay presets for different audio processing scenarios
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
        private float[]? _delayBuffer;
        private int _writeIndex;
        private int _readIndex;
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

            SampleRate = sampleRate;
            Time = time;
            Repeat = repeat;
            Mix = mix;

            InitializeBuffer();
        }

        /// <summary>
        /// Set delay parameters using predefined presets
        /// </summary>
        public void SetPreset(DelayPreset preset)
        {
            switch (preset)
            {
                case DelayPreset.SlapBack:
                    // Short slap-back delay for vocal thickening
                    // Very short timing with minimal feedback for natural doubling
                    Time = 80;          // 80ms - classic slap-back timing
                    Repeat = 0.15f;     // 15% feedback - minimal repeats
                    Mix = 0.25f;        // 25% mix - subtle but noticeable
                    Damping = 0.1f;     // Low damping for brightness
                    break;

                case DelayPreset.ClassicEcho:
                    // Traditional echo effect for general use
                    // Medium timing with controlled feedback
                    Time = 375;         // 375ms - classic echo timing (dotted eighth at 120 BPM)
                    Repeat = 0.35f;     // 35% feedback - musical repeats
                    Mix = 0.3f;         // 30% mix - balanced blend
                    Damping = 0.25f;    // Moderate damping for natural sound
                    break;

                case DelayPreset.Ambient:
                    // Spacious delay for atmospheric effects
                    // Long timing with higher feedback for ethereal sounds
                    Time = 650;         // 650ms - long, spacious timing
                    Repeat = 0.55f;     // 55% feedback - multiple repeats
                    Mix = 0.45f;        // 45% mix - prominent delay
                    Damping = 0.4f;     // Higher damping for warmth
                    break;

                case DelayPreset.Rhythmic:
                    // Eighth note delay for rhythmic enhancement
                    // Synced to musical timing for groove
                    Time = 250;         // 250ms - eighth note at 120 BPM
                    Repeat = 0.4f;      // 40% feedback - rhythmic repeats
                    Mix = 0.35f;        // 35% mix - clear rhythmic pattern
                    Damping = 0.2f;     // Moderate damping for clarity
                    break;

                case DelayPreset.PingPong:
                    // Stereo ping-pong delay effect
                    // Medium timing for stereo bouncing
                    Time = 300;         // 300ms - good for stereo width
                    Repeat = 0.45f;     // 45% feedback - sustained bouncing
                    Mix = 0.4f;         // 40% mix - prominent stereo effect
                    Damping = 0.15f;    // Low damping for stereo clarity
                    break;

                case DelayPreset.TapeEcho:
                    // Vintage tape delay emulation
                    // Classic analog delay characteristics
                    Time = 400;         // 400ms - classic tape delay timing
                    Repeat = 0.5f;      // 50% feedback - vintage sustain
                    Mix = 0.38f;        // 38% mix - vintage blend
                    Damping = 0.6f;     // High damping for tape warmth
                    break;

                case DelayPreset.Dub:
                    // Reggae/dub style delay with heavy feedback
                    // Long delay with high feedback for classic dub echoes
                    Time = 500;         // 500ms - dub timing
                    Repeat = 0.7f;      // 70% feedback - heavy dub repeats
                    Mix = 0.5f;         // 50% mix - prominent dub effect
                    Damping = 0.45f;    // Moderate-high damping for dub character
                    break;

                case DelayPreset.Thickening:
                    // Micro delay for subtle thickening
                    // Very short delay for enhancement without obvious delay
                    Time = 15;          // 15ms - micro delay timing
                    Repeat = 0.05f;     // 5% feedback - minimal repeats
                    Mix = 0.15f;        // 15% mix - subtle enhancement
                    Damping = 0.05f;    // Very low damping for transparency
                    break;
            }
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

                // Apply high-frequency damping for more natural sound
                delayedSample = _lastOutput + _dampingCoeff * (delayedSample - _lastOutput);
                _lastOutput = delayedSample;

                // Create feedback with soft clipping to prevent harsh distortion
                float feedbackSample = SoftClip(samples[i] + (delayedSample * Repeat));

                _delayBuffer[_writeIndex] = feedbackSample;

                // Mix original and delayed signals
                samples[i] = (samples[i] * (1.0f - Mix)) + (delayedSample * Mix);

                // Advance buffer pointers with wraparound
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
            _delaySamples = Math.Max(1, (int)((Time / 1000.0) * SampleRate));
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

        /// <summary>
        /// Converts milliseconds to samples based on sample rate
        /// </summary>
        public int MsToSamples(float milliseconds)
        {
            return (int)((milliseconds / 1000.0f) * SampleRate);
        }

        /// <summary>
        /// Converts samples to milliseconds based on sample rate
        /// </summary>
        public float SamplesToMs(int samples)
        {
            return (samples * 1000.0f) / SampleRate;
        }

        /// <summary>
        /// Calculates delay time for musical note values at given BPM
        /// </summary>
        /// <param name="bpm">Beats per minute</param>
        /// <param name="noteValue">Note value (1.0 = quarter note, 0.5 = eighth note, etc.)</param>
        /// <returns>Delay time in milliseconds</returns>
        public static int CalculateMusicalDelay(float bpm, float noteValue)
        {
            // Calculate quarter note duration in milliseconds
            float quarterNoteMs = (60.0f / bpm) * 1000.0f;
            return (int)(quarterNoteMs * noteValue);
        }
    }
}
