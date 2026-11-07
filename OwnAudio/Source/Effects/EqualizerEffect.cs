using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// EQ curves, the genre ones for a full mix, the instrument ones for a single track.
    /// The values are the curve itself, nothing above +6 dB.
    /// </summary>
    public enum EqualizerPreset
    {
        /// <summary>
        /// Flat, everything at 0 dB.
        /// </summary>
        Default,

        /// <summary>
        /// +6 dB low shelf, a touch of 250 Hz out so it stays clean.
        /// </summary>
        Bass,

        /// <summary>
        /// Shelf from 2k up to +5 dB.
        /// </summary>
        Treble,

        /// <summary>
        /// Smile curve, mids scooped at 500 Hz, bite at 4k.
        /// </summary>
        Rock,

        /// <summary>
        /// Almost flat, a little air.
        /// </summary>
        Classical,

        /// <summary>
        /// Kick and vocal presence up, mud out.
        /// </summary>
        Pop,

        /// <summary>
        /// Warm low mids, soft top.
        /// </summary>
        Jazz,

        /// <summary>
        /// Speech / podcast: rumble cut, 2-4k intelligibility, soft top.
        /// </summary>
        Voice,

        /// <summary>
        /// Low cut under 80 Hz, chest at 125, mud out, presence and air.
        /// </summary>
        MaleVocal,

        /// <summary>
        /// Higher low cut, 250 Hz body, 4k presence, 16k air.
        /// </summary>
        FemaleVocal,

        /// <summary>
        /// Boom and box cut, string detail and sparkle up.
        /// </summary>
        AcousticGuitar,

        /// <summary>
        /// Fundamental at 62 Hz, mud out, growl and attack, string noise rolled off.
        /// </summary>
        BassGuitar,

        /// <summary>
        /// Rumble and 250 Hz mud out, hammer definition and air.
        /// </summary>
        Piano,

        /// <summary>
        /// Drum bus: kick punch, 500 Hz box cut, snare crack, cymbals.
        /// </summary>
        Drums,

        /// <summary>
        /// Low cut, warmth, honk out, bite at 2-4k, shrill 8k tamed.
        /// </summary>
        Brass
    }

    /// <summary>
    /// 10 band peaking EQ. Two cascaded biquads per band, only the boosted/cut bands get processed.
    /// A band gain is the curve at its centre, the filters run the solved gains (GetFilterGain).
    /// </summary>
    public sealed class EqualizerEffect : NativeBackedEffect, IEffectProcessor
    {
        private const int Bands = 10;

        /// <summary>
        /// Q of both biquads of a band, the native engine has it baked in.
        /// </summary>
        public const float BandQ = 1.0f;

        private static readonly float[] _centres = { 31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
        private static readonly float[] _qs = { BandQ, BandQ, BandQ, BandQ, BandQ, BandQ, BandQ, BandQ, BandQ, BandQ };

        private readonly float[] _gains;
        private readonly float[] _filterGains = new float[Bands];
        private readonly GraphicEqSolver _solver = new GraphicEqSolver(Bands, 2, 0.005, 18f);

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

            _solver.Retune(_centres, _qs, sampleRate);
            _solver.Solve(_gains, _filterGains);
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
            _solver.Solve(_gains, _filterGains);
        }

        /// <summary>
        /// What the band's two biquads run together, in dB (half of it each).
        /// </summary>
        public float GetFilterGain(int band) => band < 0 || band >= Bands ? 0f : _filterGains[band];

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
        /// Loads one of the canned curves. Bands run 31Hz to 16kHz, the numbers are the curve
        /// at each centre (the solver does the rest).
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(EqualizerPreset preset)
        {
            switch (preset)
            {
                case EqualizerPreset.Bass:           _setGains(   6,    5, 2.5f,   -1,     0,     0,    0,    0,     0,     0); break;
                case EqualizerPreset.Treble:         _setGains(   0,    0,    0,    0,     0,     0, 1.5f,    3,  4.5f,     5); break;
                case EqualizerPreset.Rock:           _setGains(   4,    4,    2,   -1, -2.5f,    -1, 1.5f, 3.5f,     3,     2); break;
                case EqualizerPreset.Classical:      _setGains(1.5f,    1,    0,    0, -0.5f,     0,    0, 0.5f,  1.5f,     2); break;
                case EqualizerPreset.Pop:            _setGains(   1,    2,    1,   -1,     0,     1, 2.5f, 2.5f,     2,     2); break;
                case EqualizerPreset.Jazz:           _setGains(   1,    2, 2.5f, 1.5f,  0.5f,     0,    0,-0.5f,    -1, -1.5f); break;
                case EqualizerPreset.Voice:          _setGains(  -9,   -6,   -2,-1.5f,     0,     1,    3, 3.5f,     1,    -2); break;
                case EqualizerPreset.MaleVocal:      _setGains( -12,   -6,    1,   -2,    -1,     0,    1,    3,     2,  2.5f); break;
                case EqualizerPreset.FemaleVocal:    _setGains( -12,   -9,   -2,    1,    -2,     0, 0.5f, 2.5f,  1.5f,     3); break;
                case EqualizerPreset.AcousticGuitar: _setGains( -12,   -6,   -2,-2.5f, -0.5f,    -1,    1,    2,  2.5f,     3); break;
                case EqualizerPreset.BassGuitar:     _setGains(   0,    3, 0.5f,-2.5f,     0,     2, 2.5f,    0,    -2,    -4); break;
                case EqualizerPreset.Piano:          _setGains(  -3,    0, 0.5f,   -2,    -1,     0,    1,    2,  1.5f,  2.5f); break;
                case EqualizerPreset.Drums:          _setGains(1.5f, 3.5f,    1,-1.5f, -3.5f, -0.5f, 1.5f, 2.5f,     2,     3); break;
                case EqualizerPreset.Brass:          _setGains( -12,   -6,   -1, 1.5f,    -1,     0, 1.5f, 1.5f, -1.5f,     1); break;
                default:                             _setGains(   0,    0,    0,    0,     0,     0,    0,    0,     0,     0); break;
            }
        }

        /// <summary>
        /// Drops in a whole curve at once.
        /// </summary>
        private void _setGains(float g0, float g1, float g2, float g3, float g4, float g5, float g6, float g7, float g8, float g9)
        {
            _gains[0] = g0; _gains[1] = g1; _gains[2] = g2; _gains[3] = g3; _gains[4] = g4;
            _gains[5] = g5; _gains[6] = g6; _gains[7] = g7; _gains[8] = g8; _gains[9] = g9;
            _solver.Solve(_gains, _filterGains);
        }

        private protected override void OnInitialize(AudioConfig config)
        {
            if (_solver.TunedSampleRate == config.SampleRate) return;

            _solver.Retune(_centres, _qs, config.SampleRate);
            _solver.Solve(_gains, _filterGains);
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString() => $"Equalizer: Enabled={Enabled}";
    }
}
