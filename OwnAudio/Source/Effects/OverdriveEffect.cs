using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Overdrive presets for different musical styles and instruments
    /// </summary>
    public enum OverdrivePreset
    {
        /// <summary>
        /// Default overdrive settings - balanced parameters for general use
        /// Medium gain, neutral tone, standard mix and output levels
        /// </summary>
        Default,

        /// <summary>
        /// Clean boost - subtle warmth and presence without heavy distortion
        /// Low gain, balanced tone, maintains natural character
        /// </summary>
        CleanBoost,

        /// <summary>
        /// Blues overdrive - warm, musical saturation for blues and classic rock
        /// Medium gain, warm tone, vintage tube-like character
        /// </summary>
        Blues,

        /// <summary>
        /// Rock crunch - aggressive midrange punch for rock rhythm guitar
        /// Higher gain, bright tone, cutting through mix
        /// </summary>
        RockCrunch,

        /// <summary>
        /// Lead guitar - sustained overdrive for solos and lead parts
        /// High gain, bright tone, enhanced sustain
        /// </summary>
        Lead,

        /// <summary>
        /// Vintage tube - emulates classic tube amplifier breakup
        /// Medium gain, warm tone, natural tube saturation
        /// </summary>
        VintugeTube,

        /// <summary>
        /// Bass overdrive - tailored for bass instruments
        /// Lower gain, darker tone, maintains low-end definition
        /// </summary>
        Bass,

        /// <summary>
        /// Screamer - inspired by classic tube screamer pedals
        /// Medium-high gain, mid-focused tone, tight response
        /// </summary>
        Screamer
    }

    /// <summary>
    /// Overdrive effect with tube-like saturation
    /// </summary>
    public sealed class OverdriveEffect : IEffectProcessor
    {
        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig _config = null!;

        private float _gain = 2.0f;
        private float _tone = 0.5f;
        private float _mix = 1.0f;
        private float _outputLevel = 0.7f;

        // Tone control filters
        private float _lowPassState = 0.0f;
        private float _highPassState = 0.0f;

        /// <summary>
        /// Gets the unique identifier for this effect instance
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Gets the name of this effect
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Gets or sets whether this effect is enabled
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Input gain (1.0 - 5.0). Controls the amount of overdrive.
        /// </summary>
        public float Gain
        {
            get => _gain;
            set => _gain = Math.Clamp(value, 1.0f, 5.0f);
        }

        /// <summary>
        /// Tone control (0.0 - 1.0). 0.0 = dark, 1.0 = bright.
        /// </summary>
        public float Tone
        {
            get => _tone;
            set => _tone = Math.Clamp(value, 0.0f, 1.0f);
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
        /// Output level (0.1 - 1.0).
        /// </summary>
        public float OutputLevel
        {
            get => _outputLevel;
            set => _outputLevel = Math.Clamp(value, 0.1f, 1.0f);
        }

        /// <summary>
        /// Initialize Overdrive Processor with individual parameters.
        /// </summary>
        /// <param name="gain">Input gain (1.0 - 5.0)</param>
        /// <param name="tone">Tone control (0.0 - 1.0)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="outputLevel">Output level (0.1 - 1.0)</param>
        public OverdriveEffect(float gain = 2.0f, float tone = 0.5f, float mix = 1.0f, float outputLevel = 0.7f)
        {
            _id = Guid.NewGuid();
            _name = "Overdrive";
            _enabled = true;

            Gain = gain;
            Tone = tone;
            Mix = mix;
            OutputLevel = outputLevel;
        }

        /// <summary>
        /// Initialize Overdrive Processor with a preset.
        /// </summary>
        /// <param name="preset">Preset to use for initialization</param>
        public OverdriveEffect(OverdrivePreset preset)
        {
            _id = Guid.NewGuid();
            _name = "Overdrive";
            _enabled = true;

            SetPreset(preset);
        }

        /// <summary>
        /// Initialize the effect with audio configuration
        /// </summary>
        /// <param name="config">Audio configuration</param>
        public void Initialize(AudioConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Set overdrive parameters using predefined presets
        /// </summary>
        /// <param name="preset">The preset to apply</param>
        public void SetPreset(OverdrivePreset preset)
        {
            switch (preset)
            {
                case OverdrivePreset.Default:
                    // Default overdrive settings - balanced parameters for general use
                    // Medium gain, neutral tone, standard mix and output levels
                    Gain = 2.0f;        // Default medium gain
                    Tone = 0.5f;        // Neutral tone
                    Mix = 1.0f;         // Full wet signal
                    OutputLevel = 0.7f; // Standard output level
                    break;

                case OverdrivePreset.CleanBoost:
                    // Subtle warmth and presence boost without heavy distortion
                    // Low gain for transparency, balanced tone, full mix
                    Gain = 1.3f;        // Minimal overdrive, just adding warmth
                    Tone = 0.6f;        // Slightly bright for presence
                    Mix = 0.7f;         // Blend with dry signal for naturalness
                    OutputLevel = 0.9f; // Higher output to compensate for low gain
                    break;

                case OverdrivePreset.Blues:
                    // Warm, musical saturation for blues and classic rock
                    // Medium gain, warm tone for vintage tube character
                    Gain = 2.2f;        // Sweet spot for blues overdrive
                    Tone = 0.35f;       // Warm, woody tone
                    Mix = 1.0f;         // Full wet signal for classic sound
                    OutputLevel = 0.75f; // Moderate output level
                    break;

                case OverdrivePreset.RockCrunch:
                    // Aggressive midrange punch for rock rhythm guitar
                    // Higher gain, bright tone for cutting through mix
                    Gain = 3.2f;        // Aggressive drive for rock crunch
                    Tone = 0.75f;       // Bright and cutting
                    Mix = 1.0f;         // Full overdrive sound
                    OutputLevel = 0.7f; // Standard output level
                    break;

                case OverdrivePreset.Lead:
                    // Sustained overdrive for solos and lead parts
                    // High gain, bright tone, enhanced sustain
                    Gain = 4.2f;        // High gain for sustain and saturation
                    Tone = 0.8f;        // Bright for lead cut-through
                    Mix = 1.0f;         // Pure overdrive signal
                    OutputLevel = 0.65f; // Slightly lower to prevent clipping
                    break;

                case OverdrivePreset.VintugeTube:
                    // Classic tube amplifier breakup emulation
                    // Medium gain, warm tone, natural tube saturation
                    Gain = 2.8f;        // Natural tube breakup level
                    Tone = 0.4f;        // Warm vintage character
                    Mix = 0.9f;         // Mostly overdriven with hint of dry
                    OutputLevel = 0.8f; // Vintage-appropriate level
                    break;

                case OverdrivePreset.Bass:
                    // Tailored for bass instruments
                    // Lower gain, darker tone, maintains low-end definition
                    Gain = 1.8f;        // Gentle overdrive preserving fundamentals
                    Tone = 0.25f;       // Dark tone to maintain bass character
                    Mix = 0.8f;         // Blend to preserve clean low-end
                    OutputLevel = 0.85f; // Higher output for bass presence
                    break;

                case OverdrivePreset.Screamer:
                    // Classic tube screamer style overdrive
                    // Medium-high gain, mid-focused tone, tight response
                    Gain = 3.5f;        // Classic screamer drive level
                    Tone = 0.65f;       // Mid-focused, slightly bright
                    Mix = 1.0f;         // Full effect for classic screamer sound
                    OutputLevel = 0.7f; // Balanced output level
                    break;
            }
        }

        /// <summary>
        /// Process samples with overdrive effect.
        /// </summary>
        /// <param name="buffer">Input buffer</param>
        /// <param name="frameCount">Number of frames to process</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled)
                return;

            if (_mix < 0.001f)
                return;

            int sampleCount = frameCount * _config.Channels;

            for (int i = 0; i < sampleCount; i++)
            {
                float input = buffer[i];

                float gained = input * Gain;

                float overdriven = TubeSaturation(gained);

                overdriven = ApplyToneControl(overdriven);

                overdriven *= OutputLevel;

                buffer[i] = (input * (1.0f - Mix)) + (overdriven * Mix);
            }
        }

        /// <summary>
        /// Reset overdrive effect state.
        /// </summary>
        public void Reset()
        {
            _lowPassState = 0.0f;
            _highPassState = 0.0f;
        }

        /// <summary>
        /// Dispose of resources
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        /// <summary>
        /// Returns a string representation of this effect
        /// </summary>
        public override string ToString()
        {
            return $"{_name} (Enabled: {_enabled}, Mix: {_mix:F2}, Gain: {_gain:F2})";
        }

        /// <summary>
        /// Tube-like asymmetric saturation
        /// </summary>
        private static float TubeSaturation(float input)
        {
            if (input >= 0)
            {
                return (float)(Math.Tanh(input * 0.7) * 1.2);
            }
            else
            {
                return (float)(Math.Tanh(input * 0.9) * 0.9);
            }
        }

        /// <summary>
        /// Simple tone control using low-pass and high-pass filtering
        /// </summary>
        private float ApplyToneControl(float input)
        {
            float lowPassCutoff = 0.1f + (Tone * 0.4f);
            float highPassCutoff = 0.05f + ((1.0f - Tone) * 0.2f);

            _lowPassState += lowPassCutoff * (input - _lowPassState);
            _highPassState += highPassCutoff * (input - _highPassState);

            return _lowPassState - _highPassState * (1.0f - Tone);
        }
    }
}
