using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Professional quality reverb effect implementation based on the Freeverb algorithm.
    /// Suitable for real-time audio processing and professional sound quality production.
    /// </summary>
    public class Reverb : SampleProcessorBase
    {
        /// <summary>
        /// An all-pass filter implementation that performs a phase shift on the signal
        /// without changing the frequency spectrum.
        /// </summary>
        private class AllPassFilter
        {
            private readonly float[] buffer;        // Delay buffer
            private int index;                      // Current buffer position
            private readonly float gain;            // Filter amplification

            /// <summary>
            /// Initializes a new all-pass filter.
            /// </summary>
            /// <param name="size">Buffer size in samples.</param>
            /// <param name="gain">Filter gain (usually around 0.5f).</param>
            public AllPassFilter(int size, float gain)
            {
                buffer = new float[size];
                this.gain = gain;
            }

            /// <summary>
            /// Processes an input pattern.
            /// </summary>
            /// <param name="input">Input pattern.</param>
            /// <returns>Processed pattern.</returns>
            public float Process(float input)
            {
                float bufout = buffer[index];
                float temp = input * -gain + bufout;
                buffer[index] = input + (bufout * gain);
                index = (index + 1) % buffer.Length;
                return temp;
            }

            /// <summary>
            /// Clears the internal state of the filter.
            /// </summary>
            public void Clear() => Array.Clear(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Comb filter implementation that creates repetitive feedback
        /// with variable feedback and damping values.
        /// </summary>
        private class CombFilter
        {
            private readonly float[] buffer;        // Delay buffer
            private int index;                      // Current buffer position
            private float feedback;                 // Feedback rate
            private float damp1;                    // Damping parameter
            private float damp2;                    // 1 - damp1
            private float filtered;                 // Previous filtered sample

            /// <summary>
            /// Initializes a new comb filter.
            /// </summary>
            /// <param name="size">Buffer size in samples.</param>
            public CombFilter(int size)
            {
                buffer = new float[size];
                feedback = 0.5f;
                damp1 = 0.2f;
                damp2 = 1f - damp1;
            }

            /// <summary>
            /// Sets the amount of feedback.
            /// </summary>
            /// <param name="value">Feedback value (0.0 - 1.0).</param>
            public void SetFeedback(float value) => feedback = value;

            /// <summary>
            /// Sets the amount of damping.
            /// </summary>
            /// <param name="value">Damping value (0.0 - 1.0).</param>
            public void SetDamp(float value)
            {
                damp1 = value;
                damp2 = 1f - value;
            }

            /// <summary>
            /// Processes an input pattern.
            /// </summary>
            /// <param name="input">Input pattern.</param>
            /// <returns>Processed pattern.</returns>
            public float Process(float input)
            {
                float output = buffer[index];
                filtered = (output * damp2) + (filtered * damp1);
                buffer[index] = input + (filtered * feedback);
                index = (index + 1) % buffer.Length;
                return output;
            }

            /// <summary>
            /// Clears the internal state of the filter.
            /// </summary>
            public void Clear()
            {
                Array.Clear(buffer, 0, buffer.Length);
                filtered = 0f;
            }
        }

        // Freeverb constants
        private const int NUM_COMBS = 8;           // Comb filterek száma
        private const int NUM_ALLPASSES = 4;       // Number of all-pass filters

        // Filter components
        private readonly CombFilter[] combFilters;
        private readonly AllPassFilter[] allPassFilters;

        // Delay times in samples (optimized for 44.1kHz)
        private readonly float[] combTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private readonly float[] allPassTunings = { 556, 441, 341, 225 };

        // Effect parameters
        private float roomSize;       // Room size (0.0 - 1.0)
        private float damping;        // High frequency attenuation (0.0 - 1.0)
        private float width;          // Stereo width (0.0 - 1.0)
        private float wetLevel;       // Effected signal level (0.0 - 1.0)
        private float dryLevel;       // Dry signal level (0.0 - 1.0)
        private float gain;           // Input gain
        private float sampleRate;     // Sampling frequency
        private readonly object parametersLock = new object();    // Thread-safety

        /// <summary>
        /// Set the room size. A larger value results in a larger virtual space.
        /// </summary>
        public float RoomSize
        {
            get { lock (parametersLock) return roomSize; }
            set
            {
                lock (parametersLock)
                {
                    roomSize = Math.Clamp(value, 0.0f, 1.0f);
                    UpdateCombFilters();
                }
            }
        }

        /// <summary>
        /// The amount of attenuation of high frequencies.
        /// </summary>
        public float Damping
        {
            get { lock (parametersLock) return damping; }
            set
            {
                lock (parametersLock)
                {
                    damping = Math.Clamp(value, 0.0f, 1.0f);
                    UpdateDamping();
                }
            }
        }

        /// <summary>
        /// Stereo width setting.
        /// </summary>
        public float Width
        {
            get { lock (parametersLock) return width; }
            set { lock (parametersLock) width = Math.Clamp(value, 0.0f, 1.0f); }
        }

        /// <summary>
        /// Wet signal level.
        /// </summary>
        public float WetLevel
        {
            get { lock (parametersLock) return wetLevel; }
            set { lock (parametersLock) wetLevel = Math.Clamp(value, 0.0f, 1.0f); }
        }

        /// <summary>
        /// Dry signal level.
        /// </summary>
        public float DryLevel
        {
            get { lock (parametersLock) return dryLevel; }
            set { lock (parametersLock) dryLevel = Math.Clamp(value, 0.0f, 1.0f); }
        }

        /// <summary>
        /// Set the sampling frequency in Hz.
        /// </summary>
        public float SampleRate
        {
            get { lock (parametersLock) return sampleRate; }
            set
            {
                lock (parametersLock)
                {
                    if (value <= 0)
                        throw new ArgumentException("Sample rate must be positive");

                    if (Math.Abs(sampleRate - value) > 0.01f)
                    {
                        sampleRate = value;
                        InitializeFilters();
                    }
                }
            }
        }

        /// <summary>
        /// Creates a new Professional Reverb effect.
        /// </summary>
        /// <param name="size">Room size</param>
        /// <param name="damp">Treble damping</param>
        /// <param name="wet">Color of the effected signal</param>
        /// <param name="dry">Original signal level</param>
        /// <param name="stereoWidth">Width of the stereo space</param>
        /// <param name="gainLevel">Input gain</param>
        /// <param name="sampleRate">Sampling rate in Hz.</param>
        public Reverb(float size = 0.5f, float damp = 0.5f, float wet = 0.33f, float dry = 0.7f, float stereoWidth = 1.0f,float gainLevel = 0.015f, float sampleRate = 44100)
        {
            this.sampleRate = sampleRate;
            combFilters = new CombFilter[NUM_COMBS];
            allPassFilters = new AllPassFilter[NUM_ALLPASSES];

            // Setting default parameters
            roomSize = size;
            damping = damp;
            width = stereoWidth;
            wetLevel = wet;
            dryLevel = dry;
            gain = gainLevel;

            InitializeFilters();
        }

        /// <summary>
        /// Initializes or reinitializes the filters for the current sample rate.
        /// </summary>
        private void InitializeFilters()
        {
            float sampleRateScale = sampleRate / 44100f;

            // Initializing comb filters
            for (int i = 0; i < NUM_COMBS; i++)
            {
                int size = (int)(combTunings[i] * sampleRateScale);
                combFilters[i] = new CombFilter(size);
            }

            // Initializing all-pass filters
            for (int i = 0; i < NUM_ALLPASSES; i++)
            {
                int size = (int)(allPassTunings[i] * sampleRateScale);
                allPassFilters[i] = new AllPassFilter(size, 0.5f);
            }

            UpdateCombFilters();
            UpdateDamping();
        }

        /// <summary>
        /// Updates the feedback values ​​of the thigh filters based on the room size.
        /// </summary>
        private void UpdateCombFilters()
        {
            float roomFeedback = 0.7f + (roomSize * 0.28f);
            foreach (var comb in combFilters)
            {
                comb.SetFeedback(roomFeedback);
            }
        }

        /// <summary>
        /// Updates the attenuation values ​​of thigh filters.
        /// </summary>
        private void UpdateDamping()
        {
            float dampValue = damping * 0.4f;
            foreach (var comb in combFilters)
            {
                comb.SetDamp(dampValue);
            }
        }

        /// <summary>
        /// Processes a buffer of audio samples.
        /// </summary>
        /// <param name="samples">Buffer of audio samples.</param>
        public override void Process(Span<float> samples)
        {
            float currentWet, currentDry, currentWidth;
            lock (parametersLock)
            {
                currentWet = wetLevel;
                currentDry = dryLevel;
                currentWidth = width;
            }

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];
                float dry = input;
                float wet = 0;

                // Apply input gain
                input *= gain;

                // Freeverb algorithm
                float mono = 0;
                foreach (var comb in combFilters)
                {
                    mono += comb.Process(input);
                }

                foreach (var allPass in allPassFilters)
                {
                    mono = allPass.Process(mono);
                }

                // Applying stereo width and mixing
                wet = mono * currentWidth;

                // Final mixing
                samples[i] = wet * currentWet + dry * currentDry;
            }
        }

        /// <summary>
        /// Resets the effect, clearing all internal states.
        /// </summary>
        public void Reset()
        {
            foreach (var comb in combFilters)
            {
                comb.Clear();
            }

            foreach (var allPass in allPassFilters)
            {
                allPass.Clear();
            }
        }
    }
}
