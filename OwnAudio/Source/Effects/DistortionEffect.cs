using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Drive setups from gentle saturation to fuzz.
    /// </summary>
    public enum DistortionPreset
    {
        /// <summary>
        /// Moderate drive with some dry left in.
        /// </summary>
        Default,

        /// <summary>
        /// Just harmonics, barely sounds distorted.
        /// </summary>
        WarmOverdrive,

        /// <summary>
        /// Classic rock guitar amount.
        /// </summary>
        ClassicRock,

        /// <summary>
        /// High drive, fully wet.
        /// </summary>
        HeavyMetal,

        /// <summary>
        /// Tube flavoured, blended with the clean.
        /// </summary>
        VintageTube,

        /// <summary>
        /// Keeps the low end while it grinds.
        /// </summary>
        BassDrive,

        /// <summary>
        /// Extreme fuzz, low output to compensate.
        /// </summary>
        FuzzBox,

        /// <summary>
        /// Very light, only adds character to vocals.
        /// </summary>
        VocalSaturation,

        /// <summary>
        /// Harsh lo-fi grind.
        /// </summary>
        DigitalCrush
    }

    /// <summary>
    /// Drive plus soft clipping, blended back over the dry signal.
    /// </summary>
    public sealed class DistortionEffect : NativeBackedEffect, IEffectProcessor
    {
        private float _drive = 2.0f;
        private float _mix = 0.85f;
        private float _outputGain = 0.55f;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name
        {
            get => _name;
            set => _name = value ?? "Distortion";
        }

        /// <summary>
        /// Dry to wet balance.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Input drive, 1 - 10. More means more grind.
        /// </summary>
        public float Drive
        {
            get => _drive;
            set => _drive = Math.Clamp(value, 1.0f, 10.0f);
        }

        /// <summary>
        /// Output trim to pull the level back after the drive.
        /// </summary>
        public float OutputGain
        {
            get => _outputGain;
            set => _outputGain = Math.Clamp(value, 0.1f, 1.0f);
        }

        /// <summary>
        /// Builds the effect with hand picked values.
        /// </summary>
        public DistortionEffect(float drive = 2.0f, float mix = 0.85f, float outputGain = 0.55f)
            : base("Distortion")
        {
            Drive = drive;
            Mix = mix;
            OutputGain = outputGain;
        }

        /// <summary>
        /// Builds the effect from a preset.
        /// </summary>
        /// <param name="preset"></param>
        public DistortionEffect(DistortionPreset preset)
            : base("Distortion")
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(DistortionPreset preset)
        {
            switch (preset)
            {
                case DistortionPreset.Default:         Drive=2.0f; Mix=0.85f; OutputGain=0.55f; break;
                case DistortionPreset.WarmOverdrive:   Drive=1.8f; Mix=0.68f; OutputGain=0.82f; break;
                case DistortionPreset.ClassicRock:     Drive=3.5f; Mix=0.9f;  OutputGain=0.6f;  break;
                case DistortionPreset.HeavyMetal:      Drive=6.5f; Mix=1.0f;  OutputGain=0.4f;  break;
                case DistortionPreset.VintageTube:     Drive=2.2f; Mix=0.6f;  OutputGain=0.75f; break;
                case DistortionPreset.BassDrive:       Drive=2.8f; Mix=0.8f;  OutputGain=0.7f;  break;
                case DistortionPreset.FuzzBox:         Drive=8.5f; Mix=1.0f;  OutputGain=0.3f;  break;
                case DistortionPreset.VocalSaturation: Drive=1.4f; Mix=0.4f;  OutputGain=0.9f;  break;
                case DistortionPreset.DigitalCrush:    Drive=7.8f; Mix=0.95f; OutputGain=0.35f; break;
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"Distortion: Drive={_drive:F1}, Mix={_mix:F2}, OutputGain={_outputGain:F2}, Enabled={Enabled}";
        }
    }
}
