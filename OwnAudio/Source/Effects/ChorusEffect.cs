using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Chorus setups for the usual sources.
    /// </summary>
    public enum ChorusPreset
    {
        /// <summary>
        /// Safe middle ground.
        /// </summary>
        Default,

        /// <summary>
        /// Just a hint of doubling on vocals.
        /// </summary>
        VocalSubtle,

        /// <summary>
        /// Thick vocal layering.
        /// </summary>
        VocalLush,

        /// <summary>
        /// CE-1 flavoured guitar chorus.
        /// </summary>
        GuitarClassic,

        /// <summary>
        /// Fast and sparkly, clean guitar.
        /// </summary>
        GuitarShimmer,

        /// <summary>
        /// Slow, wide, dreamy. All voices in.
        /// </summary>
        SynthPad,

        /// <summary>
        /// Section-like detune spread.
        /// </summary>
        StringEnsemble,

        /// <summary>
        /// BBD style, warm and a bit seasick.
        /// </summary>
        VintageAnalog,

        /// <summary>
        /// Over the top detune/vibrato.
        /// </summary>
        Extreme
    }

    /// <summary>
    /// Multi voice chorus. LFO modulated delay taps with fractional read, mixed back over the dry.
    /// </summary>
    public sealed class ChorusEffect : NativeBackedEffect, IEffectProcessor
    {
        private float _sampleRate;

        private float _rate = 0.5f;
        private float _depth = 0.35f;
        private float _mix = 0.35f;
        private int _voices = 3;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "Chorus"; }

        /// <summary>
        /// Dry to wet balance, 0 - 1. Around a third is where a chorus still widens without
        /// sounding detuned, past 0.5 it takes over the source.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// LFO speed in Hz, 0.1 - 10.
        /// </summary>
        public float Rate
        {
            get => _rate;
            set
            {
                _rate = Math.Clamp(value, 0.1f, 10.0f);
            }
        }

        /// <summary>
        /// How far the delay time swings, 0 - 1.
        /// </summary>
        public float Depth
        {
            get => _depth;
            set => _depth = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Voice count, 2 - 6. More voices = thicker but costs CPU.
        /// </summary>
        public int Voices
        {
            get => _voices;
            set => _voices = Math.Clamp(value, 2, 6);
        }

        /// <summary>
        /// Builds the chorus with the usual slow and shallow settings. Sample rate only sizes
        /// the delay line, Initialize can override it.
        /// </summary>
        public ChorusEffect(float rate = 0.5f, float depth = 0.35f, float mix = 0.35f, int voices = 3, int sampleRate = 44100)
            : base("Chorus")
        {
            _sampleRate = sampleRate;

            _rate = rate;
            _depth = depth;
            _mix = mix;
            _voices = voices;
        }

        /// <summary>
        /// Builds the chorus from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public ChorusEffect(ChorusPreset preset, int sampleRate = 44100) : this(0.5f, 0.35f, 0.35f, 3, sampleRate)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(ChorusPreset preset)
        {
            switch (preset)
            {
                case ChorusPreset.VocalSubtle:
                    Rate=0.22f; Depth=0.14f; Mix=0.18f; Voices=2; break;
                case ChorusPreset.VocalLush:
                    Rate=0.55f; Depth=0.45f; Mix=0.32f; Voices=4; break;
                case ChorusPreset.GuitarClassic:
                    Rate=0.45f; Depth=0.32f; Mix=0.38f; Voices=3; break;
                case ChorusPreset.GuitarShimmer:
                    Rate=1.20f; Depth=0.55f; Mix=0.42f; Voices=5; break;
                case ChorusPreset.SynthPad:
                    Rate=0.12f; Depth=0.70f; Mix=0.45f; Voices=6; break;
                case ChorusPreset.StringEnsemble:
                    Rate=0.30f; Depth=0.60f; Mix=0.40f; Voices=5; break;
                case ChorusPreset.VintageAnalog:
                    Rate=0.60f; Depth=0.38f; Mix=0.36f; Voices=3; break;
                case ChorusPreset.Extreme:
                    Rate=2.40f; Depth=0.85f; Mix=0.60f; Voices=6; break;
                default:
                    Rate=0.50f; Depth=0.35f; Mix=0.35f; Voices=3; break;
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString() => $"Chorus: Rate={_rate:F2}, Depth={_depth:F2}, Enabled={Enabled}";

        /// <summary>
        /// Follows the engine rate.
        /// </summary>
        private protected override void OnInitialize(AudioConfig config)
        {
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
                _sampleRate = config.SampleRate;
        }
    }
}
