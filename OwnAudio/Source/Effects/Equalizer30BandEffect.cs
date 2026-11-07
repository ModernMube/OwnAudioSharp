using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// EQ curves for the 30 band version. Genre curves are for a full mix, the instrument
    /// ones for one track. The numbers are the curve itself, boosts stop at +6 dB.
    /// </summary>
    public enum Equalizer30Preset
    {
        /// <summary>
        /// Flat, everything at 0 dB.
        /// </summary>
        Default,

        /// <summary>
        /// +6 dB sub shelf down from 63 Hz, -1 dB at 250-400 Hz.
        /// </summary>
        Bass,

        /// <summary>
        /// Smooth lift from 1k up, +5 dB at 16k.
        /// </summary>
        Treble,

        /// <summary>
        /// Smile, scoop at 500 Hz and bite at 4k.
        /// </summary>
        Rock,

        /// <summary>
        /// Near flat with a bit of air.
        /// </summary>
        Classical,

        /// <summary>
        /// Kick, vocal presence and sparkle, mud out.
        /// </summary>
        Pop,

        /// <summary>
        /// Warm low mids, soft top.
        /// </summary>
        Jazz,

        /// <summary>
        /// Speech / podcast intelligibility: rumble down 10 dB, 2-4k up, soft top.
        /// </summary>
        Voice,

        /// <summary>
        /// Deep sub punch, low mids scooped, bright highs.
        /// </summary>
        Electronic,

        /// <summary>
        /// Natural, a little body and presence.
        /// </summary>
        Acoustic,

        /// <summary>
        /// Low cut under 80 Hz, chest at 160-200, 400 Hz mud out, presence at 4-5k and air.
        /// </summary>
        MaleVocal,

        /// <summary>
        /// Low cut under 125 Hz, body at 250, 500 Hz out, presence and 16k air, 8k sibilance held back.
        /// </summary>
        FemaleVocal,

        /// <summary>
        /// Boom (200-250 Hz) and box (800 Hz) out, string detail and sparkle.
        /// </summary>
        AcousticGuitar,

        /// <summary>
        /// Fundamental at 63-80 Hz, mud out, growl at 1k, attack at 2.5k, fret noise rolled off.
        /// </summary>
        BassGuitar,

        /// <summary>
        /// Rumble cut, 250-400 Hz mud out, hammer definition and air.
        /// </summary>
        Piano,

        /// <summary>
        /// Drum bus. Kick at 63 Hz, box cut at 400 Hz, snare crack 2.5-4k, cymbals 10-12.5k.
        /// </summary>
        Drums,

        /// <summary>
        /// Low cut, warmth at 200-250, honk out, bite at 3k, shrill 8k tamed.
        /// </summary>
        Brass
    }

    /// <summary>
    /// 30 band peaking EQ, one biquad per band to keep the phase behaviour sane. A band gain is
    /// the curve at its centre; the bells overlap, so the filters run solved gains (GetFilterGain).
    /// </summary>
    public sealed class Equalizer30BandEffect : NativeBackedEffect, IEffectProcessor
    {
        private const int BANDS = 30;

        private readonly float[] _gains;
        private readonly float[] _filterGains;
        private readonly float[] _frequencies;
        private readonly float[] _qFactors;
        private readonly GraphicEqSolver _solver = new GraphicEqSolver(BANDS, 1, 0.005, 18f);
        private float _sampleRate;

        private static readonly float[] StandardFrequencies = {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
        };

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Dry/wet. The native 30-band engine honours it; 0 is fully dry.
        /// </summary>
        public float Mix { get; set; } = 1.0f;

        /// <summary>
        /// Builds the EQ. The optional array holds 30 band gains in dB.
        /// </summary>
        public Equalizer30BandEffect(float sampleRate = 44100, float[]? gains = null)
            : base("Equalizer30Band")
        {
            _sampleRate = sampleRate;

            _gains = new float[BANDS];
            _filterGains = new float[BANDS];
            _frequencies = new float[BANDS];
            _qFactors = new float[BANDS];

            _initFilters();
            _solver.Retune(_frequencies, _qFactors, _sampleRate);

            if (gains != null && gains.Length >= BANDS)
            {
                for (int i = 0; i < BANDS; i++)
                    SetBandGain(i, StandardFrequencies[i], _qFactors[i], gains[i]);
            }
        }

        /// <summary>
        /// Builds the EQ from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public Equalizer30BandEffect(Equalizer30Preset preset, float sampleRate = 44100)
            : this(sampleRate)
        {
            SetPreset(preset);
        }

        #region Band Properties

        /// <summary>
        /// Band gain in dB by index, 0-29.
        /// </summary>
        public float this[int band]
        {
            get => GetBandGain(band);
            set => SetBandGain(band, StandardFrequencies[band], _qFactors[band], value);
        }

        /// <summary>
        /// Gain of one band in dB, 0 if the index is off.
        /// </summary>
        public float GetBandGain(int band)
        {
            if (band < 0 || band >= BANDS) return 0f;
            return _gains[band];
        }

        /// <summary>
        /// Centre frequency of one band, 0 if the index is off.
        /// </summary>
        public float GetBandFrequency(int band)
        {
            if (band < 0 || band >= BANDS) return 0f;
            return _frequencies[band];
        }

        /// <summary>
        /// What the band's filter actually runs, in dB — the bank solved so the summed curve
        /// passes through the band gains. Draw the response off these, not off GetBandGain.
        /// </summary>
        public float GetFilterGain(int band) => band < 0 || band >= BANDS ? 0f : _filterGains[band];

        /// <summary>
        /// Q of one band.
        /// </summary>
        public float GetBandQ(int band) => band < 0 || band >= BANDS ? DefaultBandQ : _qFactors[band];

        /// <summary>
        /// Copy of all 30 gains.
        /// </summary>
        public float[] GetAllGains()
        {
            float[] result = new float[BANDS];
            Array.Copy(_gains, result, BANDS);
            return result;
        }

        /// <summary>
        /// Drops in a whole curve, needs at least 30 values.
        /// </summary>
        public void SetAllGains(float[] gains)
        {
            if (gains == null || gains.Length < BANDS) return;
            for (int i = 0; i < BANDS; i++)
                SetBandGain(i, StandardFrequencies[i], _qFactors[i], gains[i]);
        }

        /// <summary>
        /// Working sample rate.
        /// </summary>
        public float SampleRate => _sampleRate;

        #endregion

        /// <summary>
        /// Bells about 0.57 octave wide on 1/3-octave centres. The 1/3-octave Q (4.32) left a
        /// 2+ dB ripple between the bands; this overlaps enough for a smooth curve, and the
        /// solver takes the overlap back out.
        /// </summary>
        public const float DefaultBandQ = 2.5f;

        /// <summary>
        /// ISO centres, constant Q, flat to start with.
        /// </summary>
        private void _initFilters()
        {
            for (int band = 0; band < BANDS; band++)
            {
                _frequencies[band] = StandardFrequencies[band];
                _qFactors[band] = DefaultBandQ;
                _gains[band] = 0.0f;
            }
        }

        /// <summary>
        /// Retunes one band completely: centre frequency, Q and gain in dB (-18 to +18).
        /// </summary>
        public void SetBandGain(int band, float frequency, float q, float gainDB)
        {
            if (band < 0 || band >= BANDS) return;

            frequency = Math.Clamp(frequency, 20.0f, 20000.0f);
            q = Math.Clamp(q, 0.1f, 10.0f);
            gainDB = Math.Clamp(gainDB, -18f, 18f);

            bool _reshaped = Math.Abs(_frequencies[band] - frequency) > 0.001f || Math.Abs(_qFactors[band] - q) > 0.001f;
            if (!_reshaped && Math.Abs(_gains[band] - gainDB) <= 0.001f) return;

            _frequencies[band] = frequency;
            _qFactors[band] = q;
            _gains[band] = gainDB;

            if (_reshaped) _solver.Retune(_frequencies, _qFactors, _sampleRate);
            _solver.Solve(_gains, _filterGains);
        }

        /// <summary>
        /// Loads one of the canned curves.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(Equalizer30Preset preset)
        {
            float[] gains = new float[BANDS];
            switch (preset)
            {
                case Equalizer30Preset.Default: break;
                case Equalizer30Preset.Bass:
                    _gainCurve(gains, new[] { (0, 6f), (5, 6f), (6, 5f), (7, 4f), (8, 2.5f), (9, 1f), (10, 0f), (11, -1f), (13, -1f), (15, 0f) }); break;
                case Equalizer30Preset.Treble:
                    _gainCurve(gains, new[] { (17, 0f), (20, 1.5f), (23, 3f), (26, 4.5f), (29, 5f) }); break;
                case Equalizer30Preset.Rock:
                    _gainCurve(gains, new[] { (0, 4f), (5, 4f), (8, 2f), (11, -1f), (14, -2.5f), (17, -1f), (20, 1.5f), (23, 3.5f), (26, 3f), (29, 2f) }); break;
                case Equalizer30Preset.Classical:
                    _gainCurve(gains, new[] { (0, 1.5f), (5, 1f), (8, 0f), (14, -0.5f), (17, 0f), (20, 0f), (23, 0.5f), (26, 1.5f), (29, 2f) }); break;
                case Equalizer30Preset.Pop:
                    _gainCurve(gains, new[] { (0, 1f), (5, 2f), (8, 1f), (11, -1f), (14, 0f), (17, 1f), (20, 2.5f), (23, 2.5f), (26, 2f), (29, 2f) }); break;
                case Equalizer30Preset.Jazz:
                    _gainCurve(gains, new[] { (0, 1f), (5, 2f), (8, 2.5f), (11, 1.5f), (14, 0.5f), (17, 0f), (20, 0f), (23, -0.5f), (26, -1f), (29, -1.5f) }); break;
                case Equalizer30Preset.Voice:
                    _gainCurve(gains, new[] { (0, -10f), (3, -8f), (5, -6f), (8, -2.5f), (11, -1.5f), (14, 0f), (17, 1f), (20, 3f), (23, 3.5f), (26, 1f), (29, -2f) }); break;
                case Equalizer30Preset.Electronic:
                    _gainCurve(gains, new[] { (0, 5f), (2, 6f), (4, 6f), (5, 5f), (7, 2.5f), (10, 0f), (13, -1.5f), (15, -1.5f), (17, 0f), (20, 1f), (23, 2.5f), (26, 4f), (29, 4.5f) }); break;
                case Equalizer30Preset.Acoustic:
                    _gainCurve(gains, new[] { (0, 0f), (5, 1f), (8, 1.5f), (11, 0.5f), (14, -0.5f), (17, 0f), (20, 1.5f), (23, 2f), (26, 1.5f), (29, 0.5f) }); break;
                case Equalizer30Preset.MaleVocal:
                    _gainCurve(gains, new[] { (0, -12f), (3, -10f), (5, -6f), (6, -3f), (7, -1f), (8, 0.5f), (10, 1f), (11, 0f), (12, -2f), (13, -2.5f), (14, -1.5f), (16, -0.5f), (17, 0f), (19, 1f), (21, 2.5f), (24, 3f), (25, 2f), (26, 1.5f), (28, 3f), (29, 2.5f) }); break;
                case Equalizer30Preset.FemaleVocal:
                    _gainCurve(gains, new[] { (0, -12f), (4, -10f), (6, -7f), (8, -3f), (9, -1f), (10, 0.5f), (11, 1f), (12, 0f), (13, -1.5f), (14, -2f), (15, -1f), (17, 0f), (20, 0.5f), (22, 1.5f), (24, 2.5f), (25, 2.5f), (26, 1.5f), (28, 3f), (29, 3f) }); break;
                case Equalizer30Preset.AcousticGuitar:
                    _gainCurve(gains, new[] { (0, -12f), (3, -9f), (5, -5f), (7, -1.5f), (8, -1.5f), (10, -2.5f), (11, -2.5f), (13, -1f), (14, -0.5f), (16, -1.5f), (18, 0f), (20, 1f), (22, 2f), (24, 2f), (25, 1.5f), (26, 2f), (27, 3f), (28, 3f), (29, 2.5f) }); break;
                case Equalizer30Preset.BassGuitar:
                    _gainCurve(gains, new[] { (0, -4f), (2, -1f), (4, 2f), (5, 3f), (6, 3f), (7, 2f), (8, 0.5f), (10, -2f), (11, -2.5f), (12, -2f), (13, -0.5f), (15, 1f), (16, 2f), (17, 2f), (19, 1f), (21, 2.5f), (22, 2f), (23, 0.5f), (24, -0.5f), (26, -2f), (29, -4f) }); break;
                case Equalizer30Preset.Piano:
                    _gainCurve(gains, new[] { (0, -6f), (2, -3f), (4, -1f), (5, 0f), (7, 0.5f), (8, 0.5f), (10, -1f), (11, -2f), (13, -2f), (14, -1f), (16, 0f), (19, 0.5f), (21, 1.5f), (23, 2f), (25, 1.5f), (27, 2f), (29, 2.5f) }); break;
                case Equalizer30Preset.Drums:
                    _gainCurve(gains, new[] { (0, -3f), (2, 1f), (4, 3f), (5, 3.5f), (6, 3f), (8, 1f), (10, -1f), (12, -3f), (13, -3.5f), (14, -3f), (15, -1.5f), (17, 0f), (19, 1f), (21, 2f), (23, 2.5f), (24, 2f), (25, 1.5f), (27, 3f), (28, 3f), (29, 2.5f) }); break;
                case Equalizer30Preset.Brass:
                    _gainCurve(gains, new[] { (0, -12f), (3, -9f), (5, -6f), (7, -2.5f), (8, -1f), (9, 0.5f), (10, 1.5f), (11, 1.5f), (12, 0.5f), (13, -0.5f), (14, -1f), (15, -1f), (16, -0.5f), (17, 0f), (19, 1f), (20, 1.5f), (22, 2f), (23, 1.5f), (24, 0.5f), (25, -0.5f), (26, -1.5f), (27, -0.5f), (28, 1f), (29, 0.5f) }); break;
            }
            SetAllGains(gains);
        }

        /// <summary>
        /// Fills the gain array by interpolating between the given band/gain key points.
        /// The values are the curve itself now, the solver handles the overlap. Bands
        /// outside the first and last key point stay flat.
        /// </summary>
        private void _gainCurve(float[] gains, (int band, float gain)[] keyPoints)
        {
            for (int i = 0; i < keyPoints.Length - 1; i++)
            {
                int startBand = keyPoints[i].band;
                int endBand = keyPoints[i + 1].band;
                float startGain = keyPoints[i].gain;
                float endGain = keyPoints[i + 1].gain;

                for (int band = startBand; band <= endBand && band < BANDS; band++)
                {
                    float t = (float)(band - startBand) / (endBand - startBand);
                    gains[band] = startGain + t * (endGain - startGain);
                }
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString() => $"Equalizer30Band [ID: {Id}, Enabled: {Enabled}]";

        /// <summary>
        /// Follows the engine rate.
        /// </summary>
        private protected override void OnInitialize(AudioConfig config)
        {
            _sampleRate = config.SampleRate;
            if (_solver.TunedSampleRate == _sampleRate) return;

            _solver.Retune(_frequencies, _qFactors, _sampleRate);
            _solver.Solve(_gains, _filterGains);
        }
    }
}
