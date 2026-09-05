using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Enhancer setups per source type.
    /// </summary>
    public enum EnhancerPreset
    {
        /// <summary>
        /// Balanced, works on most material.
        /// </summary>
        Default,

        /// <summary>
        /// Air above 4.5k, no harshness.
        /// </summary>
        VocalClarity,

        /// <summary>
        /// Upper mid bite for guitars.
        /// </summary>
        RockEdge,

        /// <summary>
        /// Barely there sparkle, keeps the instrument natural.
        /// </summary>
        AcousticSparkle,

        /// <summary>
        /// Presence push so the track cuts through a busy mix.
        /// </summary>
        MixCutter,

        /// <summary>
        /// Speech clarity without listening fatigue.
        /// </summary>
        Broadcast
    }

    /// <summary>
    /// Exciter: takes the high band out with a one pole HP, saturates it and adds it back on top.
    /// </summary>
    public sealed class EnhancerEffect : NativeBackedEffect, IEffectProcessor
    {
        private float _mix;
        private float _gain;
        private float _cutFreq;
        private float _sampleRate;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name
        {
            get => _name;
            set => _name = value ?? "Enhancer";
        }

        /// <summary>
        /// How much of the excited band we add on top, 0 - 1.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Pre saturation gain, 0.1 - 10.
        /// </summary>
        public float Gain
        {
            get => _gain;
            set => _gain = Math.Clamp(value, 0.1f, 10.0f);
        }

        /// <summary>
        /// High-pass corner, everything above this gets excited.
        /// </summary>
        public float CutoffFrequency
        {
            get => _cutFreq;
            set => _cutFreq = Math.Clamp(value, 100.0f, 20000.0f);
        }

        /// <summary>
        /// Working sample rate.
        /// </summary>
        public float SampleRate
        {
            get => _sampleRate;
            set => _sampleRate = Math.Clamp(value, 8000.0f, 192000.0f);
        }

        /// <summary>
        /// Builds the effect. Cutoff is usually 2-6k, gain 2-4x, and the blend stays under
        /// 20% — an exciter is meant to be felt, not heard.
        /// </summary>
        public EnhancerEffect(float mix = 0.15f, float cutFreq = 3500f, float gain = 1.8f, float sampleRate = 44100f)
            : base("Enhancer")
        {
            Mix = mix;
            CutoffFrequency = cutFreq;
            Gain = gain;
            SampleRate = sampleRate;
            Reset();
        }

        /// <summary>
        /// Builds the effect from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public EnhancerEffect(EnhancerPreset preset, float sampleRate = 44100f)
            : base("Enhancer")
        {
            SampleRate = sampleRate;
            SetPreset(preset);
            Reset();
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(EnhancerPreset preset)
        {
            switch (preset)
            {
                case EnhancerPreset.VocalClarity:    Mix=0.12f; CutoffFrequency=4500f; Gain=1.8f; break;
                case EnhancerPreset.RockEdge:        Mix=0.22f; CutoffFrequency=2800f; Gain=2.8f; break;
                case EnhancerPreset.AcousticSparkle: Mix=0.08f; CutoffFrequency=5500f; Gain=1.5f; break;
                case EnhancerPreset.MixCutter:       Mix=0.25f; CutoffFrequency=3200f; Gain=2.8f; break;
                case EnhancerPreset.Broadcast:       Mix=0.18f; CutoffFrequency=4000f; Gain=2.2f; break;
                default:                             Mix=0.15f; CutoffFrequency=3500f; Gain=1.8f; break;
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"Enhancer: Mix={_mix:F2}, Gain={_gain:F1}, Cutoff={_cutFreq:F0}Hz, Enabled={Enabled}";
        }
    }
}
