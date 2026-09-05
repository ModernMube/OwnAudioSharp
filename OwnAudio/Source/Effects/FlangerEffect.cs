using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Flanger setups from subtle thickening to jet sweep.
    /// </summary>
    public enum FlangerPreset
    {
        /// <summary>
        /// All purpose, audible but not in your face.
        /// </summary>
        Default,

        /// <summary>
        /// Tape style swoosh.
        /// </summary>
        Classic,

        /// <summary>
        /// Fast and deep with lots of feedback.
        /// </summary>
        JetPlane,

        /// <summary>
        /// Slow and shallow, more like a chorus.
        /// </summary>
        SubtleChorus,

        /// <summary>
        /// Natural doubling for vocals.
        /// </summary>
        VocalDoubling,

        /// <summary>
        /// Cutting lead sound for solos.
        /// </summary>
        GuitarLead,

        /// <summary>
        /// Very slow, deep and dreamy.
        /// </summary>
        AmbientWash,

        /// <summary>
        /// Fast and tight, keeps the drum attack.
        /// </summary>
        Percussive
    }

    /// <summary>
    /// Flanger: short LFO swept delay fed back into itself.
    /// </summary>
    public sealed class FlangerEffect : NativeBackedEffect, IEffectProcessor
    {
        private readonly int _sampleRate;

        private float _rate = 0.35f;
        private float _depth = 0.60f;
        private float _feedback = 0.45f;
        private float _mix = 0.40f;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// LFO speed in Hz, 0.1 - 5.
        /// </summary>
        public float Rate
        {
            get => _rate;
            set => _rate = Math.Clamp(value, 0.1f, 5.0f);
        }

        /// <summary>
        /// Sweep depth, 0 - 1.
        /// </summary>
        public float Depth
        {
            get => _depth;
            set => _depth = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Feedback, capped at 0.95 so it doesn't run away.
        /// </summary>
        public float Feedback
        {
            get => _feedback;
            set => _feedback = Math.Clamp(value, 0.0f, 0.95f);
        }

        /// <summary>
        /// Dry to wet balance. The comb notches are deepest at 0.5, so nothing above that
        /// buys you anything except a thinner dry.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Sample rate this instance was built for.
        /// </summary>
        public int SampleRate => _sampleRate;

        /// <summary>
        /// Builds the flanger with hand picked values: a slow sweep with moderate resonance,
        /// same as the Default preset.
        /// </summary>
        public FlangerEffect(float rate = 0.35f, float depth = 0.60f, float feedback = 0.45f, float mix = 0.40f, int sampleRate = 44100)
            : base("Flanger")
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;
            Rate = rate;
            Depth = depth;
            Feedback = feedback;
            Mix = mix;
        }

        /// <summary>
        /// Builds the flanger from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public FlangerEffect(FlangerPreset preset, int sampleRate = 44100)
            : base("Flanger")
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;

            SetPreset(preset);
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(FlangerPreset preset)
        {
            switch (preset)
            {
                case FlangerPreset.Default:       Rate=0.35f; Depth=0.60f; Feedback=0.45f; Mix=0.40f; break;
                case FlangerPreset.Classic:       Rate=0.50f; Depth=0.70f; Feedback=0.60f; Mix=0.45f; break;
                case FlangerPreset.JetPlane:      Rate=2.20f; Depth=0.95f; Feedback=0.88f; Mix=0.50f; break;
                case FlangerPreset.SubtleChorus:  Rate=0.22f; Depth=0.30f; Feedback=0.15f; Mix=0.25f; break;
                case FlangerPreset.VocalDoubling: Rate=0.30f; Depth=0.45f; Feedback=0.20f; Mix=0.30f; break;
                case FlangerPreset.GuitarLead:    Rate=1.10f; Depth=0.80f; Feedback=0.70f; Mix=0.48f; break;
                case FlangerPreset.AmbientWash:   Rate=0.12f; Depth=0.90f; Feedback=0.55f; Mix=0.45f; break;
                case FlangerPreset.Percussive:    Rate=3.00f; Depth=0.55f; Feedback=0.30f; Mix=0.35f; break;
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"Flanger [ID: {Id}, Enabled: {Enabled}, Rate: {_rate:F2}Hz, Depth: {_depth:F2}, Mix: {_mix:F2}]";
        }
    }
}
