﻿using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Distortion presets for different audio processing scenarios
    /// </summary>
    public enum DistortionPreset
    {
        /// <summary>
        /// Default preset with basic distortion settings
        /// Standard moderate drive and mix values for general use
        /// </summary>
        Default,

        /// <summary>
        /// Subtle warm overdrive - gentle saturation for adding character
        /// Low drive, balanced mix for musical warmth without harshness
        /// </summary>
        WarmOverdrive,

        /// <summary>
        /// Classic rock distortion - medium drive with balanced output
        /// Moderate drive and mix for classic rock guitar tones
        /// </summary>
        ClassicRock,

        /// <summary>
        /// Heavy metal distortion - aggressive saturation for modern metal
        /// High drive, full wet signal for maximum distortion impact
        /// </summary>
        HeavyMetal,

        /// <summary>
        /// Vintage tube saturation - emulates warm analog tube distortion
        /// Low to medium drive with subtle mix for vintage character
        /// </summary>
        VintageTube,

        /// <summary>
        /// Bass drive - controlled distortion optimized for bass frequencies
        /// Medium drive with careful output gain to maintain low-end presence
        /// </summary>
        BassDrive,

        /// <summary>
        /// Fuzz box - extreme vintage-style fuzzy distortion
        /// Very high drive with unique output characteristics for psychedelic sounds
        /// </summary>
        FuzzBox,

        /// <summary>
        /// Vocal saturation - gentle distortion for adding vocal character
        /// Very subtle drive perfect for vocal processing and character
        /// </summary>
        VocalSaturation,

        /// <summary>
        /// Bitcrusher style - harsh digital-style distortion
        /// High drive with reduced output gain for lo-fi digital artifacts
        /// </summary>
        DigitalCrush
    }

    /// <summary>
    /// Distortion effect with overdrive and soft clipping
    /// </summary>
    public class Distortion : SampleProcessorBase
    {
        private float _drive = 2.0f;
        private float _mix = 1.0f;
        private float _outputGain = 0.5f;

        /// <summary>
        /// Drive amount (1.0 - 10.0). Higher values create more distortion.
        /// </summary>
        public float Drive
        {
            get => _drive;
            set => _drive = Math.Clamp(value, 1.0f, 10.0f);
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
        /// Output gain compensation (0.1 - 1.0).
        /// </summary>
        public float OutputGain
        {
            get => _outputGain;
            set => _outputGain = Math.Clamp(value, 0.1f, 1.0f);
        }

        /// <summary>
        /// Initialize Distortion Processor with all parameters.
        /// </summary>
        /// <param name="drive">Drive amount (1.0 - 10.0)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="outputGain">Output gain (0.1 - 1.0)</param>
        public Distortion(float drive = 2.0f, float mix = 1.0f, float outputGain = 0.5f)
        {
            Drive = drive;
            Mix = mix;
            OutputGain = outputGain;
        }

        /// <summary>
        /// Initialize Distortion Processor with preset selection.
        /// </summary>
        /// <param name="preset">Distortion preset to use</param>
        public Distortion(DistortionPreset preset)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Set distortion parameters using predefined presets
        /// </summary>
        public void SetPreset(DistortionPreset preset)
        {
            switch (preset)
            {
                case DistortionPreset.Default:
                    // Default preset with basic distortion settings
                    // Standard moderate values matching the default constructor parameters
                    Drive = 2.0f;         // Basic drive amount
                    Mix = 1.0f;           // Full wet signal
                    OutputGain = 0.5f;    // Standard output compensation
                    break;

                case DistortionPreset.WarmOverdrive:
                    // Subtle warm saturation for adding musical character
                    // Low drive for gentle saturation, partial mix to retain clarity
                    Drive = 1.8f;         // Gentle overdrive amount
                    Mix = 0.7f;           // 70% wet - retains some clean signal
                    OutputGain = 0.8f;    // Higher output to compensate for gentle processing
                    break;

                case DistortionPreset.ClassicRock:
                    // Balanced rock distortion for classic guitar tones
                    // Medium drive with full wet signal for traditional rock sound
                    Drive = 3.5f;         // Classic rock drive level
                    Mix = 0.9f;           // 90% wet - mostly distorted with slight clean blend
                    OutputGain = 0.6f;    // Balanced output level
                    break;

                case DistortionPreset.HeavyMetal:
                    // Aggressive saturation for modern metal tones
                    // High drive with full wet signal for maximum impact
                    Drive = 6.5f;         // High drive for heavy saturation
                    Mix = 1.0f;           // 100% wet - full distortion
                    OutputGain = 0.4f;    // Lower output due to high drive level
                    break;

                case DistortionPreset.VintageTube:
                    // Warm analog tube emulation
                    // Medium-low drive with subtle mix for vintage warmth
                    Drive = 2.2f;         // Tube-style gentle saturation
                    Mix = 0.6f;           // 60% wet - warm blend with clean signal
                    OutputGain = 0.75f;   // Warm, present output level
                    break;

                case DistortionPreset.BassDrive:
                    // Controlled distortion optimized for bass frequencies
                    // Medium drive with careful output management for low-end
                    Drive = 2.8f;         // Bass-friendly drive amount
                    Mix = 0.8f;           // 80% wet - maintains some clean low-end
                    OutputGain = 0.7f;    // Preserves bass presence
                    break;

                case DistortionPreset.FuzzBox:
                    // Extreme vintage-style fuzzy distortion
                    // Very high drive for classic fuzz box characteristics
                    Drive = 8.5f;         // Extreme fuzz saturation
                    Mix = 1.0f;           // 100% wet - full fuzz effect
                    OutputGain = 0.3f;    // Low output due to extreme drive
                    break;

                case DistortionPreset.VocalSaturation:
                    // Gentle saturation for vocal processing
                    // Very subtle drive perfect for adding vocal character
                    Drive = 1.4f;         // Very gentle saturation
                    Mix = 0.4f;           // 40% wet - subtle character enhancement
                    OutputGain = 0.9f;    // High output to maintain vocal presence
                    break;

                case DistortionPreset.DigitalCrush:
                    // Harsh digital-style distortion for lo-fi effects
                    // High drive with specific output characteristics
                    Drive = 7.8f;         // High drive for digital artifacts
                    Mix = 0.95f;          // 95% wet - almost full digital processing
                    OutputGain = 0.35f;   // Lower output for harsh digital character
                    break;
            }
        }

        /// <summary>
        /// Process samples with distortion effect.
        /// </summary>
        /// <param name="samples">Input samples</param>
        public override void Process(Span<float> samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                float driven = input * Drive;

                float distorted = SoftClip(driven);

                distorted *= OutputGain;

                samples[i] = (input * (1.0f - Mix)) + (distorted * Mix);
            }
        }

        /// <summary>
        /// Reset distortion effect (no internal state to clear).
        /// </summary>
        public override void Reset()
        {
            // No internal state to reset
        }

        /// <summary>
        /// Soft clipping function for smooth distortion
        /// </summary>
        private static float SoftClip(float input)
        {
            if (Math.Abs(input) <= 1.0f)
                return input;

            return Math.Sign(input) * (2.0f - 2.0f / (Math.Abs(input) + 1.0f));
        }
    }
}
