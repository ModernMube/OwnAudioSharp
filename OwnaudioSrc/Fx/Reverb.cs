using Ownaudio.Processors;
using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Reverb presets for different acoustic environments and audio processing scenarios
    /// </summary>
    public enum ReverbPreset
    {
        /// <summary>
        /// Default preset - balanced reverb settings suitable for general use
        /// Medium room size, moderate damping, professional balance
        /// </summary>
        Default,

        /// <summary>
        /// Small room reverb - intimate acoustic space simulation
        /// Short decay, minimal damping, suitable for vocals and intimate recordings
        /// </summary>
        SmallRoom,

        /// <summary>
        /// Large hall reverb - spacious concert hall simulation
        /// Long decay, moderate damping, creates sense of grandeur and space
        /// </summary>
        LargeHall,

        /// <summary>
        /// Cathedral reverb - Gothic cathedral acoustic simulation
        /// Very long decay, low damping, ethereal and majestic sound
        /// </summary>
        Cathedral,

        /// <summary>
        /// Plate reverb - vintage studio plate reverb emulation
        /// Medium decay, bright character, classic studio sound from the 60s-80s
        /// </summary>
        Plate,

        /// <summary>
        /// Spring reverb - vintage spring tank reverb emulation
        /// Short to medium decay, characteristic metallic resonance, surf guitar classic
        /// </summary>
        Spring,

        /// <summary>
        /// Ambient pad - lush atmospheric reverb
        /// Very long decay, wide stereo image, perfect for pads and ambient textures
        /// </summary>
        AmbientPad,

        /// <summary>
        /// Vocal booth - controlled vocal reverb
        /// Short decay, balanced tone, professional vocal enhancement without muddiness
        /// </summary>
        VocalBooth,

        /// <summary>
        /// Drum room - punchy drum reverb
        /// Medium decay, quick attack, adds space without losing transient impact
        /// </summary>
        DrumRoom,

        /// <summary>
        /// Gated reverb - 80s style gated reverb effect
        /// Abrupt cutoff, dramatic effect, classic 80s drum sound
        /// </summary>
        Gated,

        /// <summary>
        /// Subtle enhancement - minimal natural reverb
        /// Very short decay, natural sound, adds life without obvious reverb effect
        /// </summary>
        Subtle
    }

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
                    roomSize = FastClamp(value);
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
                    damping = FastClamp(value);
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
            set { lock (parametersLock) width = FastClamp(value); }
        }

        /// <summary>
        /// Wet signal level.
        /// </summary>
        public float WetLevel
        {
            get { lock (parametersLock) return wetLevel; }
            set { lock (parametersLock) wetLevel = FastClamp(value); }
        }

        /// <summary>
        /// Dry signal level.
        /// </summary>
        public float DryLevel
        {
            get { lock (parametersLock) return dryLevel; }
            set { lock (parametersLock) dryLevel = FastClamp(value); }
        }

        /// <summary>
        /// Input gain level.
        /// </summary>
        public float Gain
        {
            get { lock (parametersLock) return gain; }
            set { lock (parametersLock) gain = Math.Max(0.0f, value); } // Gain can be > 1.0
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
        /// Creates a new Professional Reverb effect with all parameters.
        /// </summary>
        /// <param name="size">Room size (0.0 - 1.0)</param>
        /// <param name="damp">Treble damping (0.0 - 1.0)</param>
        /// <param name="wet">Wet signal level (0.0 - 1.0)</param>
        /// <param name="dry">Dry signal level (0.0 - 1.0)</param>
        /// <param name="stereoWidth">Stereo width (0.0 - 1.0)</param>
        /// <param name="gainLevel">Input gain (>= 0.0)</param>
        /// <param name="sampleRate">Sampling rate in Hz</param>
        public Reverb(float size = 0.5f, float damp = 0.5f, float wet = 0.33f, float dry = 0.7f, float stereoWidth = 1.0f, float gainLevel = 0.015f, float sampleRate = 44100)
        {
            this.sampleRate = sampleRate;
            combFilters = new CombFilter[NUM_COMBS];
            allPassFilters = new AllPassFilter[NUM_ALLPASSES];

            // Setting parameters with validation
            roomSize = FastClamp(size);
            damping = FastClamp(damp);
            width = FastClamp(stereoWidth);
            wetLevel = FastClamp(wet);
            dryLevel = FastClamp(dry);
            gain = Math.Max(0.0f, gainLevel);

            InitializeFilters();
        }

        /// <summary>
        /// Creates a new Professional Reverb effect using a preset.
        /// </summary>
        /// <param name="preset">The reverb preset to use</param>
        /// <param name="sampleRate">Sampling rate in Hz</param>
        public Reverb(ReverbPreset preset, float sampleRate = 44100)
        {
            this.sampleRate = sampleRate;
            combFilters = new CombFilter[NUM_COMBS];
            allPassFilters = new AllPassFilter[NUM_ALLPASSES];

            // Initialize with default values first
            roomSize = 0.5f;
            damping = 0.5f;
            width = 1.0f;
            wetLevel = 0.33f;
            dryLevel = 0.7f;
            gain = 0.015f;

            InitializeFilters();

            // Apply the preset
            SetPreset(preset);
        }

        /// <summary>
        /// Set reverb parameters using predefined presets
        /// </summary>
        public void SetPreset(ReverbPreset preset)
        {
            switch (preset)
            {
                case ReverbPreset.Default:
                    // Balanced professional reverb settings
                    RoomSize = 0.5f;      // Medium room size
                    Damping = 0.5f;       // Balanced damping
                    WetLevel = 0.33f;     // Moderate reverb presence
                    DryLevel = 0.7f;      // Clear dry signal
                    Width = 1.0f;         // Full stereo width
                    Gain = 0.015f;        // Standard input gain
                    break;

                case ReverbPreset.SmallRoom:
                    // Intimate room sound - cozy acoustic space
                    RoomSize = 0.3f;      // Small space simulation
                    Damping = 0.4f;       // Moderate high frequency absorption
                    WetLevel = 0.25f;     // Subtle reverb presence
                    DryLevel = 0.85f;     // Strong dry signal
                    Width = 0.7f;         // Moderate stereo width
                    Gain = 0.015f;        // Standard input gain
                    break;

                case ReverbPreset.LargeHall:
                    // Concert hall acoustics - spacious and grand
                    RoomSize = 0.85f;     // Large space simulation
                    Damping = 0.3f;       // Less damping for brightness
                    WetLevel = 0.5f;      // Significant reverb presence
                    DryLevel = 0.6f;      // Balanced dry signal
                    Width = 1.0f;         // Full stereo width
                    Gain = 0.015f;        // Standard input gain
                    break;

                case ReverbPreset.Cathedral:
                    // Gothic cathedral - ethereal and majestic
                    RoomSize = 0.95f;     // Very large space
                    Damping = 0.15f;      // Minimal damping for long decay
                    WetLevel = 0.6f;      // Strong reverb presence
                    DryLevel = 0.5f;      // Balanced for ethereal effect
                    Width = 1.0f;         // Full stereo width
                    Gain = 0.015f;        // Standard input gain
                    break;

                case ReverbPreset.Plate:
                    // Vintage plate reverb - bright studio classic
                    RoomSize = 0.6f;      // Medium decay time
                    Damping = 0.2f;       // Bright, less damped character
                    WetLevel = 0.4f;      // Classic studio reverb amount
                    DryLevel = 0.7f;      // Professional balance
                    Width = 0.8f;         // Wide but controlled
                    Gain = 0.015f;        // Standard input gain
                    break;

                case ReverbPreset.Spring:
                    // Spring tank reverb - surf guitar classic
                    RoomSize = 0.4f;      // Shorter decay typical of springs
                    Damping = 0.1f;       // Very bright, metallic character
                    WetLevel = 0.35f;     // Characteristic spring reverb amount
                    DryLevel = 0.75f;     // Guitar amp style balance
                    Width = 0.6f;         // Moderate stereo spread
                    Gain = 0.015f;        // Standard input gain
                    break;

                case ReverbPreset.AmbientPad:
                    // Lush atmospheric reverb - perfect for pads
                    RoomSize = 0.9f;      // Very long decay
                    Damping = 0.25f;      // Smooth high frequency roll-off
                    WetLevel = 0.7f;      // Heavily processed for atmosphere
                    DryLevel = 0.4f;      // Less dry signal for ambience
                    Width = 1.0f;         // Maximum stereo width
                    Gain = 0.015f;        // Standard input gain
                    break;

                case ReverbPreset.VocalBooth:
                    // Professional vocal reverb - controlled enhancement
                    RoomSize = 0.35f;     // Short to medium decay
                    Damping = 0.5f;       // Balanced frequency response
                    WetLevel = 0.3f;      // Subtle enhancement
                    DryLevel = 0.8f;      // Clear vocal presence
                    Width = 0.5f;         // Focused stereo image
                    Gain = 0.015f;        // Standard input gain
                    break;

                case ReverbPreset.DrumRoom:
                    // Drum room acoustics - punchy with space
                    RoomSize = 0.5f;      // Medium room size
                    Damping = 0.6f;       // Control reflections for punch
                    WetLevel = 0.35f;     // Add space without mud
                    DryLevel = 0.8f;      // Preserve transient impact
                    Width = 0.9f;         // Wide drum image
                    Gain = 0.015f;        // Standard input gain
                    break;

                case ReverbPreset.Gated:
                    // 80s gated reverb - dramatic cutoff effect
                    RoomSize = 0.7f;      // Medium-large for dramatic effect
                    Damping = 0.8f;       // Heavy damping for gated character
                    WetLevel = 0.6f;      // Strong effect presence
                    DryLevel = 0.7f;      // Balanced for dramatic impact
                    Width = 1.0f;         // Full width for 80s sound
                    Gain = 0.015f;        // Standard input gain
                    break;

                case ReverbPreset.Subtle:
                    // Minimal natural enhancement - adds life
                    RoomSize = 0.2f;      // Very short decay
                    Damping = 0.7f;       // Natural absorption
                    WetLevel = 0.15f;     // Very subtle presence
                    DryLevel = 0.95f;     // Mostly dry signal
                    Width = 0.4f;         // Narrow, natural width
                    Gain = 0.015f;        // Standard input gain
                    break;
            }
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
            float currentWet, currentDry, currentWidth, currentGain;
            lock (parametersLock)
            {
                currentWet = wetLevel;
                currentDry = dryLevel;
                currentWidth = width;
                currentGain = gain;
            }

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];
                float dry = input;

                // Apply input gain
                input *= currentGain;

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
                float wet = mono * currentWidth;

                // Final mixing
                samples[i] = wet * currentWet + dry * currentDry;
            }
        }

        /// <summary>
        /// Resets the effect, clearing all internal states but preserving parameters.
        /// </summary>
        public override void Reset()
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

        /// <summary>
        /// Fast audio clamping function that constrains values to the valid audio range [0.0, 1.0].
        /// </summary>
        /// <param name="value">The audio parameter value to clamp.</param>
        /// <returns>The clamped value within the range [0.0, 1.0].</returns>
        /// <remarks>
        /// This method is aggressively inlined for maximum performance in audio processing loops.
        /// Parameter clamping is essential to prevent:
        /// - Invalid parameter states
        /// - Unexpected audio artifacts
        /// - Filter instability
        /// 
        /// Values below 0.0 are clamped to 0.0, values above 1.0 are clamped to 1.0,
        /// and values within the valid range are passed through unchanged.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastClamp(float value)
        {
            return value < 0.0f ? 0.0f : (value > 1.0f ? 1.0f : value);
        }
    }
}
