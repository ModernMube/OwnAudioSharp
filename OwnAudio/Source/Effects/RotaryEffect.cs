using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Leslie cabinet setups per style.
    /// </summary>
    public enum RotaryPreset
    {
        /// <summary>
        /// Balanced starting point.
        /// </summary>
        Default,

        /// <summary>
        /// Classic Hammond cabinet.
        /// </summary>
        Hammond,

        /// <summary>
        /// Warm and expressive gospel movement.
        /// </summary>
        Gospel,

        /// <summary>
        /// Aggressive fast Leslie for rock.
        /// </summary>
        Rock,

        /// <summary>
        /// Gentle and refined, jazz combo.
        /// </summary>
        Jazz,

        /// <summary>
        /// Extreme, deliberately unnatural doppler.
        /// </summary>
        Psychedelic,

        /// <summary>
        /// Authentic slow cabinet.
        /// </summary>
        VintageSlow,

        /// <summary>
        /// Leslie 122 in tremolo: 6.7Hz horn, 5.2Hz rotor.
        /// </summary>
        VintageFast,

        /// <summary>
        /// Barely moving, background texture.
        /// </summary>
        Subtle
    }

    /// <summary>
    /// Rotary speaker sim. The signal is split at 800Hz, the horn takes the top and the
    /// rotor the bottom, each with its own doppler delay and tremolo.
    /// </summary>
    public sealed class RotaryEffect : NativeBackedEffect, IEffectProcessor
    {
        /// <summary>
        /// Chorale to tremolo ratio of a Leslie 122: the horn goes from about 40rpm to 400,
        /// the bass drum is geared a bit lower.
        /// </summary>
        private const float FastHornRatio = 9.0f;
        private const float FastRotorRatio = 8.0f;

        private float _hornSpeed = 0.8f;
        private float _rotorSpeed = 0.7f;
        private float _intensity = 0.7f;
        private float _mix = 1.0f;
        private bool _isFast = false;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Horn chorale speed in Hz, 0.4 - 8. A real 122 idles around 0.8Hz here and the fast
        /// switch takes it to tremolo, roughly 6.7Hz.
        /// </summary>
        public float HornSpeed
        {
            get => _hornSpeed;
            set => _hornSpeed = Math.Clamp(value, 0.4f, 8.0f);
        }

        /// <summary>
        /// Rotor chorale speed in Hz, 0.3 - 6. The bass drum is slower than the horn, both
        /// standing still and in tremolo.
        /// </summary>
        public float RotorSpeed
        {
            get => _rotorSpeed;
            set => _rotorSpeed = Math.Clamp(value, 0.3f, 6.0f);
        }

        /// <summary>
        /// How deep the doppler and the tremolo go.
        /// </summary>
        public float Intensity
        {
            get => _intensity;
            set => _intensity = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Dry to wet balance. A cabinet is the whole sound, not a layer, so this belongs at
        /// 1.0 unless you deliberately want a parallel blend.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Fast/slow cabinet switch.
        /// </summary>
        public bool IsFast
        {
            get => _isFast;
            set => _isFast = value;
        }

        /// <summary>
        /// Builds the cabinet with hand picked values, a 122 sitting in chorale.
        /// </summary>
        public RotaryEffect(float hornSpeed = 0.8f, float rotorSpeed = 0.7f, float intensity = 0.7f, float mix = 1.0f, bool isFast = false, int sampleRate = 44100)
            : base("Rotary")
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            HornSpeed = hornSpeed;
            RotorSpeed = rotorSpeed;
            Intensity = intensity;
            Mix = mix;
            IsFast = isFast;
        }

        /// <summary>
        /// Builds the cabinet from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public RotaryEffect(RotaryPreset preset, int sampleRate = 44100)
            : this(0.8f, 0.7f, 0.7f, 1.0f, false, sampleRate)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Loads one of the canned setups. Speeds are the pre-switch values, the fast
        /// presets get their real rate from the x3 / x2 multipliers.
        /// </summary>
        public void SetPreset(RotaryPreset preset)
        {
            switch (preset)
            {
                case RotaryPreset.Hammond:     HornSpeed=0.80f; RotorSpeed=0.70f; Intensity=0.75f; Mix=1.0f;  IsFast=false; break;
                case RotaryPreset.Gospel:      HornSpeed=0.85f; RotorSpeed=0.72f; Intensity=0.85f; Mix=1.0f;  IsFast=true;  break;
                case RotaryPreset.Rock:        HornSpeed=0.78f; RotorSpeed=0.68f; Intensity=0.90f; Mix=1.0f;  IsFast=true;  break;
                case RotaryPreset.Jazz:        HornSpeed=0.70f; RotorSpeed=0.60f; Intensity=0.60f; Mix=1.0f;  IsFast=false; break;
                case RotaryPreset.Psychedelic: HornSpeed=1.10f; RotorSpeed=0.90f; Intensity=1.0f;  Mix=1.0f;  IsFast=true;  break;
                case RotaryPreset.VintageSlow: HornSpeed=0.75f; RotorSpeed=0.65f; Intensity=0.70f; Mix=1.0f;  IsFast=false; break;
                case RotaryPreset.VintageFast: HornSpeed=0.75f; RotorSpeed=0.65f; Intensity=0.78f; Mix=1.0f;  IsFast=true;  break;
                case RotaryPreset.Subtle:      HornSpeed=0.60f; RotorSpeed=0.50f; Intensity=0.35f; Mix=0.65f; IsFast=false; break;
                default:                       HornSpeed=0.80f; RotorSpeed=0.70f; Intensity=0.70f; Mix=1.0f;  IsFast=false; break;
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"{_name} (Enabled: {Enabled}, Mix: {_mix:F2}, Speed: {(_isFast ? "Fast" : "Slow")})";
        }
    }
}
