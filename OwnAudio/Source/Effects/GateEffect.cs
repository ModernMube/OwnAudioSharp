using System;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Gate setups per source type.
    /// </summary>
    public enum GatePreset
    {
        /// <summary>
        /// Careful general purpose setting.
        /// </summary>
        Default,

        /// <summary>
        /// Keeps room tone and headphone bleed out between phrases.
        /// </summary>
        Vocal,

        /// <summary>
        /// Fast and short, for close mic'd toms and snare.
        /// </summary>
        DrumTight,

        /// <summary>
        /// Kills amp hiss and single coil hum when the player stops.
        /// </summary>
        GuitarNoise,

        /// <summary>
        /// Speech with a long hold, so a breath does not slam the gate shut.
        /// </summary>
        Broadcast,

        /// <summary>
        /// Only takes the very bottom off, nothing you can hear working.
        /// </summary>
        Subtle
    }

    /// <summary>
    /// Noise gate. The detector follows the loudest channel of each frame and the same
    /// envelope opens every channel, so the stereo image stays put. The open and close
    /// thresholds sit a few dB apart, which is what stops it chattering on a signal
    /// hovering right at the line.
    /// </summary>
    public sealed class GateEffect : NativeBackedEffect, IEffectProcessor
    {
        private readonly int _sampleRate;

        private float _threshold = -40.0f;
        private float _attack = 1.0f;
        private float _release = 100.0f;
        private float _hold = 50.0f;
        private float _mix = 1.0f;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Open threshold in dB, -80 - 0. Anything quieter than this gets shut once the
        /// hold window runs out.
        /// </summary>
        public float Threshold
        {
            get => _threshold;
            set => _threshold = Math.Clamp(value, -80.0f, 0.0f);
        }

        /// <summary>
        /// Opening time in ms, 0.1 - 100. Percussion wants the low end of that, anything
        /// slow enough to hear is a fade in.
        /// </summary>
        public float Attack
        {
            get => _attack;
            set => _attack = Math.Clamp(value, 0.1f, 100.0f);
        }

        /// <summary>
        /// Closing time in ms, 10 - 2000.
        /// </summary>
        public float Release
        {
            get => _release;
            set => _release = Math.Clamp(value, 10.0f, 2000.0f);
        }

        /// <summary>
        /// How long the gate stays where it is after the signal drops, 0 - 500 ms. This is
        /// the knob that keeps a decaying note from being chopped.
        /// </summary>
        public float Hold
        {
            get => _hold;
            set => _hold = Math.Clamp(value, 0.0f, 500.0f);
        }

        /// <summary>
        /// Gated to dry balance. Below 1.0 you get an expander rather than a gate.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Working sample rate.
        /// </summary>
        public int SampleRate => _sampleRate;

        /// <summary>
        /// Builds the gate with hand picked values. Threshold is in dB, the three times
        /// in ms.
        /// </summary>
        public GateEffect(float threshold = -40.0f, float attack = 1.0f, float release = 100.0f,
            float hold = 50.0f, float mix = 1.0f, int sampleRate = 44100)
            : base("Gate")
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;

            Threshold = threshold;
            Attack = attack;
            Release = release;
            Hold = hold;
            Mix = mix;
        }

        /// <summary>
        /// Builds the gate from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public GateEffect(GatePreset preset, int sampleRate = 44100)
            : this(sampleRate: sampleRate)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(GatePreset preset)
        {
            switch (preset)
            {
                case GatePreset.Vocal:       Threshold=-38.0f; Attack=2.0f; Release=180.0f; Hold=80.0f;  Mix=1.0f; break;
                case GatePreset.DrumTight:   Threshold=-28.0f; Attack=0.2f; Release=60.0f;  Hold=20.0f;  Mix=1.0f; break;
                case GatePreset.GuitarNoise: Threshold=-45.0f; Attack=1.0f; Release=120.0f; Hold=40.0f;  Mix=1.0f; break;
                case GatePreset.Broadcast:   Threshold=-42.0f; Attack=3.0f; Release=250.0f; Hold=150.0f; Mix=1.0f; break;
                case GatePreset.Subtle:      Threshold=-60.0f; Attack=5.0f; Release=400.0f; Hold=200.0f; Mix=0.8f; break;
                default:                     Threshold=-40.0f; Attack=1.0f; Release=100.0f; Hold=50.0f;  Mix=1.0f; break;
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"{_name} (Enabled: {Enabled}, Threshold: {_threshold:F1}dB, Hold: {_hold:F0}ms, Mix: {_mix:F2})";
        }
    }
}
