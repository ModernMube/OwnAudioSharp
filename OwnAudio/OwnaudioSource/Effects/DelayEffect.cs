using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Delay presets for different audio processing scenarios
    /// </summary>
    public enum DelayPreset
    {
        /// <summary>
        /// Default delay settings - balanced delay with moderate parameters
        /// Medium delay time with controlled feedback for general use
        /// </summary>
        Default,

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
    public sealed class DelayEffect : IEffectProcessor
    {
        // IEffectProcessor implementation
        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;

        // Audio configuration
        private AudioConfig? _config;

        private float[]? _delayBuffer;
        private int _writeIndex;
        private int _readIndex;
        private int _delaySamples;
        private int _time;
        private float _repeat;
        private float _mix;
        private int _sampleRate;

        // High-frequency damping for more natural sound
        private float _dampingCoeff = 0.2f;
        private float _lastOutput = 0.0f;

        /// <summary>
        /// Gets the unique identifier for this effect.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Gets or sets the name of the effect.
        /// </summary>
        public string Name
        {
            get => _name;
            set => _name = value ?? "Delay";
        }

        /// <summary>
        /// Gets or sets whether the effect is enabled.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// The delay time is in milliseconds.
        /// Automatically updates buffer length when modified.
        /// </summary>
        public int Time
        {
            get => _time;
            set
            {
                _time = Math.Max(1, Math.Min(5000, value)); // Min: 1ms, Max: 5000ms
                UpdateDelayTime();
            }
        }

        /// <summary>
        /// The amount of feedback (value between 0.0 and 1.0).
        /// </summary>
        public float Repeat
        {
            get => _repeat;
            set => _repeat = Math.Max(0.0f, Math.Min(1.0f, value));
        }

        /// <summary>
        /// The mixing ratio of the original and delayed signal (value between 0.0 and 1.0).
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Max(0.0f, Math.Min(1.0f, value));
        }

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
        public int SampleRate
        {
            get => _sampleRate;
            set
            {
                _sampleRate = Math.Max(8000, Math.Min(192000, value)); // Min: 8kHz, Max: 192kHz
                UpdateDelayTime();
            }
        }

        /// <summary>
        /// Initialize Delay Processor with default settings.
        /// </summary>
        public DelayEffect()
        {
            _id = Guid.NewGuid();
            _name = "Delay";
            _enabled = true;

            _sampleRate = 44100;
            _time = 375;
            _repeat = 0.35f;
            _mix = 0.3f;
            _dampingCoeff = 0.25f;

            InitializeBuffer();
        }

        /// <summary>
        /// Initialize Delay Processor with a specific preset.
        /// </summary>
        /// <param name="preset">The delay preset to use</param>
        public DelayEffect(DelayPreset preset) : this()
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Initialize Delay Processor with custom parameters.
        /// </summary>
        /// <param name="time">The delay time is in milliseconds.</param>
        /// <param name="repeat">The feedback rate (0.0 - 1.0).</param>
        /// <param name="mix">The mixing ratio of the original and delayed signal (0.0 - 1.0).</param>
        /// <param name="damping">High-frequency damping coefficient (0.0 - 1.0).</param>
        /// <param name="sampleRate">The sampling frequency (Hz).</param>
        public DelayEffect(int time, float repeat, float mix, float damping = 0.25f, int sampleRate = 44100)
        {
            _id = Guid.NewGuid();
            _name = "Delay";
            _enabled = true;

            _sampleRate = Math.Max(8000, Math.Min(192000, sampleRate));
            Time = time;
            Repeat = repeat;
            Mix = mix;
            Damping = damping;

            InitializeBuffer();
        }

        /// <summary>
        /// Set delay parameters using predefined presets
        /// </summary>
        public void SetPreset(DelayPreset preset)
        {
            switch (preset)
            {
                case DelayPreset.Default:
                    // Default balanced delay settings
                    Time = 375;         // 375ms - classic delay timing
                    Repeat = 0.35f;     // 35% feedback - balanced repeats
                    Mix = 0.3f;         // 30% mix - moderate blend
                    Damping = 0.25f;    // Moderate damping for natural sound
                    break;

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
        /// Initializes the effect with the specified audio configuration.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // Update sample rate from config
            _sampleRate = config.SampleRate;
            UpdateDelayTime();
        }

        /// <summary>
        /// Sample processing with delay.
        /// </summary>
        /// <param name="buffer">The input samples</param>
        /// <param name="frameCount">The number of frames in the buffer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled)
                return;

            // Fast path: if mix is 0, no processing needed
            if (_mix < 0.001f)
                return;

            if (_delayBuffer == null || _delayBuffer.Length == 0)
                return;

            // Calculate the actual number of samples to process
            int sampleCount = frameCount * _config.Channels;

            for (int i = 0; i < sampleCount; i++)
            {
                float delayedSample = _delayBuffer[_readIndex];

                // Apply high-frequency damping for more natural sound
                delayedSample = _lastOutput + _dampingCoeff * (delayedSample - _lastOutput);
                _lastOutput = delayedSample;

                // Create feedback with soft clipping to prevent harsh distortion
                float feedbackSample = SoftClip(buffer[i] + (delayedSample * _repeat));

                _delayBuffer[_writeIndex] = feedbackSample;

                // Mix original and delayed signals
                buffer[i] = (buffer[i] * (1.0f - _mix)) + (delayedSample * _mix);

                // Advance buffer pointers with wraparound
                _writeIndex = (_writeIndex + 1) % _delaySamples;
                _readIndex = (_readIndex + 1) % _delaySamples;
            }
        }

        /// <summary>
        /// It clears temporary storage but does not change parameters.
        /// </summary>
        public void Reset()
        {
            // Clear buffer and reset indices - but keep current parameters!
            if (_delayBuffer != null)
            {
                Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            }
            _writeIndex = 0;
            _readIndex = 0;
            _lastOutput = 0.0f;
        }

        /// <summary>
        /// Disposes the delay effect and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            Reset();
            _delayBuffer = null;

            _disposed = true;
        }

        /// <summary>
        /// Update the delay time.
        /// </summary>
        private void UpdateDelayTime()
        {
            _delaySamples = Math.Max(1, (int)((_time / 1000.0) * _sampleRate));
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
            return (int)((milliseconds / 1000.0f) * _sampleRate);
        }

        /// <summary>
        /// Converts samples to milliseconds based on sample rate
        /// </summary>
        public float SamplesToMs(int samples)
        {
            return (samples * 1000.0f) / _sampleRate;
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

        /// <summary>
        /// Returns a string representation of the effect's state.
        /// </summary>
        public override string ToString()
        {
            return $"Delay: Time={_time}ms, Repeat={_repeat:F2}, Mix={_mix:F2}, Damping={_dampingCoeff:F2}, Enabled={_enabled}";
        }
    }
}
