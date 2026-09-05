using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Overdrive setups per instrument and style.
    /// </summary>
    public enum OverdrivePreset
    {
        /// <summary>
        /// Balanced starting point.
        /// </summary>
        Default,

        /// <summary>
        /// Barely any grit, just warmth and presence.
        /// </summary>
        CleanBoost,

        /// <summary>
        /// Warm and woody, blues sweet spot.
        /// </summary>
        Blues,

        /// <summary>
        /// Bright and aggressive rhythm crunch.
        /// </summary>
        RockCrunch,

        /// <summary>
        /// High gain with sustain, for solos.
        /// </summary>
        Lead,

        /// <summary>
        /// Classic tube breakup.
        /// </summary>
        VintugeTube,

        /// <summary>
        /// Dark and gentle, keeps the bottom end.
        /// </summary>
        Bass,

        /// <summary>
        /// Tube screamer flavour, mid focused.
        /// </summary>
        Screamer
    }

    /// <summary>
    /// Overdrive with asymmetric tube-ish saturation and a simple tone control.
    /// </summary>
    public sealed class OverdriveEffect : IEffectProcessor
    {
        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private readonly NativeEffectEngine _native = new NativeEffectEngine();
        private AudioConfig _config = null!;

        private float _gain = 2.0f;
        private float _tone = 0.5f;
        private float _mix = 1.0f;
        private float _outputLevel = 0.7f;

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Input gain, 1 - 5.
        /// </summary>
        public float Gain
        {
            get => _gain;
            set => _gain = Math.Clamp(value, 1.0f, 5.0f);
        }

        /// <summary>
        /// Tone knob, 0 is dark and 1 is bright.
        /// </summary>
        public float Tone
        {
            get => _tone;
            set => _tone = Math.Clamp(value, 0.0f, 1.0f);
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
        /// Output trim, 0.1 - 1.
        /// </summary>
        public float OutputLevel
        {
            get => _outputLevel;
            set => _outputLevel = Math.Clamp(value, 0.1f, 1.0f);
        }

        /// <summary>
        /// Builds the effect with hand picked values.
        /// </summary>
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
        /// Builds the effect from a preset.
        /// </summary>
        /// <param name="preset"></param>
        public OverdriveEffect(OverdrivePreset preset)
        {
            _id = Guid.NewGuid();
            _name = "Overdrive";
            _enabled = true;

            SetPreset(preset);
        }

        /// <summary>
        /// Stores the engine config.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config;
            _native.Initialize(this, config);
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(OverdrivePreset preset)
        {
            switch (preset)
            {
                case OverdrivePreset.CleanBoost:
                    Gain = 1.3f; Tone = 0.6f; Mix = 1.0f; OutputLevel = 0.9f;
                    break;

                case OverdrivePreset.Blues:
                    Gain = 2.2f; Tone = 0.35f; Mix = 1.0f; OutputLevel = 0.75f;
                    break;

                case OverdrivePreset.RockCrunch:
                    Gain = 3.2f; Tone = 0.75f; Mix = 1.0f; OutputLevel = 0.7f;
                    break;

                case OverdrivePreset.Lead:
                    Gain = 4.2f; Tone = 0.8f; Mix = 1.0f; OutputLevel = 0.65f;
                    break;

                case OverdrivePreset.VintugeTube:
                    Gain = 2.8f; Tone = 0.4f; Mix = 0.9f; OutputLevel = 0.8f;
                    break;

                case OverdrivePreset.Bass:
                    Gain = 1.8f; Tone = 0.25f; Mix = 0.8f; OutputLevel = 0.85f;
                    break;

                case OverdrivePreset.Screamer:
                    Gain = 3.5f; Tone = 0.65f; Mix = 1.0f; OutputLevel = 0.7f;
                    break;

                default:
                    Gain = 2.0f; Tone = 0.5f; Mix = 1.0f; OutputLevel = 0.7f;
                    break;
            }
        }

        /// <summary>
        /// Same DSP the mixer twin runs, on this instance's native handle.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount)
        {
            _native.Process(this, buffer, frameCount);
        }

        /// <summary>
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        /// <summary>
        /// Clears the tone filter memory.
        /// </summary>
        public void Reset()
        {
            ResetGeneration++;
            _native.Reset();
        }

        /// <summary>
        /// Nothing unmanaged here.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _native.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"{_name} (Enabled: {_enabled}, Mix: {_mix:F2}, Gain: {_gain:F2})";
        }
    }
}
