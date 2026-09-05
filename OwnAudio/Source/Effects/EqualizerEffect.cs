using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// EQ curves for the usual listening scenarios.
    /// </summary>
    public enum EqualizerPreset
    {
        /// <summary>
        /// Flat, everything at 0 dB.
        /// </summary>
        Default,

        /// <summary>
        /// Low end lift.
        /// </summary>
        Bass,

        /// <summary>
        /// Top end lift.
        /// </summary>
        Treble,

        /// <summary>
        /// V-curve with a 2k bite.
        /// </summary>
        Rock,

        /// <summary>
        /// Almost flat, just a bit of air.
        /// </summary>
        Classical,

        /// <summary>
        /// Vocal forward with sparkle on top.
        /// </summary>
        Pop,

        /// <summary>
        /// Warm low mids.
        /// </summary>
        Jazz,

        /// <summary>
        /// Presence peak, extremes rolled off.
        /// </summary>
        Voice
    }

    /// <summary>
    /// 10 band peaking EQ. Two cascaded biquads per band, only the boosted/cut bands get processed.
    /// </summary>
    public sealed class EqualizerEffect : NativeBackedEffect, IEffectProcessor
    {
        private const int Bands = 10;

        private readonly float[] _gains;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "Equalizer"; }

        /// <summary>
        /// Dry/wet. The native engine honours it; 0 is fully dry.
        /// </summary>
        public float Mix { get; set; } = 1.0f;

        /// <summary>
        /// Builds the EQ, band gains are in dB on the ISO centres from 31.25Hz up to 16kHz.
        /// </summary>
        public EqualizerEffect(float sampleRate = 44100,
                        float band0Gain = 0.0f, float band1Gain = 0.0f, float band2Gain = 0.0f, float band3Gain = 0.0f, float band4Gain = 0.0f,
                        float band5Gain = 0.0f, float band6Gain = 0.0f, float band7Gain = 0.0f, float band8Gain = 0.0f, float band9Gain = 0.0f)
            : base("Equalizer")
        {
            _gains = new float[Bands];

            _gains[0] = band0Gain; _gains[1] = band1Gain; _gains[2] = band2Gain; _gains[3] = band3Gain; _gains[4] = band4Gain;
            _gains[5] = band5Gain; _gains[6] = band6Gain; _gains[7] = band7Gain; _gains[8] = band8Gain; _gains[9] = band9Gain;
        }

        /// <summary>
        /// Builds the EQ from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public EqualizerEffect(EqualizerPreset preset, float sampleRate = 44100) : this(sampleRate)
        {
            SetPreset(preset);
        }

        #region Band Propertyes

        /// <summary>
        /// 31.25 Hz gain in dB.
        /// </summary>
        public float Band0Gain { get => _gains[0]; set => _setBand(0, value); }

        /// <summary>
        /// 62.5 Hz gain in dB.
        /// </summary>
        public float Band1Gain { get => _gains[1]; set => _setBand(1, value); }

        /// <summary>
        /// 125 Hz gain in dB.
        /// </summary>
        public float Band2Gain { get => _gains[2]; set => _setBand(2, value); }

        /// <summary>
        /// 250 Hz gain in dB.
        /// </summary>
        public float Band3Gain { get => _gains[3]; set => _setBand(3, value); }

        /// <summary>
        /// 500 Hz gain in dB.
        /// </summary>
        public float Band4Gain { get => _gains[4]; set => _setBand(4, value); }

        /// <summary>
        /// 1 kHz gain in dB.
        /// </summary>
        public float Band5Gain { get => _gains[5]; set => _setBand(5, value); }

        /// <summary>
        /// 2 kHz gain in dB.
        /// </summary>
        public float Band6Gain { get => _gains[6]; set => _setBand(6, value); }

        /// <summary>
        /// 4 kHz gain in dB.
        /// </summary>
        public float Band7Gain { get => _gains[7]; set => _setBand(7, value); }

        /// <summary>
        /// 8 kHz gain in dB.
        /// </summary>
        public float Band8Gain { get => _gains[8]; set => _setBand(8, value); }

        /// <summary>
        /// 16 kHz gain in dB.
        /// </summary>
        public float Band9Gain { get => _gains[9]; set => _setBand(9, value); }

        #endregion

        /// <summary>
        /// Sets one band gain in dB, the mirror picks it up from there.
        /// </summary>
        private void _setBand(int index, float gain)
        {
            if (index < 0 || index >= Bands) return;
            if (Math.Abs(_gains[index] - gain) <= 0.01f) return;

            _gains[index] = Math.Clamp(gain, -12f, 12f);
        }

        /// <summary>
        /// Sets one band gain in dB. frequency and q are ignored: the native EQ runs the
        /// fixed ISO centres at Q=1 and has no param for either. Use Equalizer30BandEffect
        /// if you need to move a bell.
        /// </summary>
        public void SetBandGain(int band, float frequency, float q, float gainDB)
        {
            if (band < 0 || band >= Bands) return;

            _setBand(band, gainDB);
        }

        /// <summary>
        /// Loads one of the canned curves. Bands run 31Hz to 16kHz.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(EqualizerPreset preset)
        {
            switch (preset)
            {
                case EqualizerPreset.Bass:      _setGains( 6,  5,  3, -1, -1,  0,  0,  1,  2,  1); break;
                case EqualizerPreset.Treble:    _setGains( 0,  0,  0,  0,  1,  2,  4,  4,  5,  3); break;
                case EqualizerPreset.Rock:      _setGains( 4,  3,  1, -2, -2,  0,  3,  4,  3,  2); break;
                case EqualizerPreset.Classical: _setGains( 1,  0,  0,  0, -1,  0,  1,  1,  2,  2); break;
                case EqualizerPreset.Pop:       _setGains( 2,  1,  0,  1,  3,  3,  3,  2,  2,  2); break;
                case EqualizerPreset.Jazz:      _setGains( 3,  2,  2,  1,  0,  0,  0,  1,  1, -1); break;
                case EqualizerPreset.Voice:     _setGains(-3, -2,  0,  2,  5,  5,  4,  2, -1, -2); break;
                default:                        _setGains( 0,  0,  0,  0,  0,  0,  0,  0,  0,  0); break;
            }
        }

        /// <summary>
        /// Drops in a whole curve at once.
        /// </summary>
        private void _setGains(float g0, float g1, float g2, float g3, float g4, float g5, float g6, float g7, float g8, float g9)
        {
            _gains[0] = g0; _gains[1] = g1; _gains[2] = g2; _gains[3] = g3; _gains[4] = g4;
            _gains[5] = g5; _gains[6] = g6; _gains[7] = g7; _gains[8] = g8; _gains[9] = g9;
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString() => $"Equalizer: Enabled={Enabled}";
    }
}
