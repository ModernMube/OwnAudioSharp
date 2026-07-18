using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// EQ curves for the 30 band version.
    /// </summary>
    public enum Equalizer30Preset
    {
        /// <summary>
        /// Flat, everything at 0 dB.
        /// </summary>
        Default,

        /// <summary>
        /// Sub and low end lift.
        /// </summary>
        Bass,

        /// <summary>
        /// Smooth lift from 1k up.
        /// </summary>
        Treble,

        /// <summary>
        /// V-curve with bite at 3k.
        /// </summary>
        Rock,

        /// <summary>
        /// Near flat with a bit of air.
        /// </summary>
        Classical,

        /// <summary>
        /// Vocal forward with top end sparkle.
        /// </summary>
        Pop,

        /// <summary>
        /// Warm low mids, soft top.
        /// </summary>
        Jazz,

        /// <summary>
        /// Intelligibility peak, extremes cut.
        /// </summary>
        Voice,

        /// <summary>
        /// Deep sub punch and bright highs.
        /// </summary>
        Electronic,

        /// <summary>
        /// Natural room sound.
        /// </summary>
        Acoustic
    }

    /// <summary>
    /// 30 band peaking EQ, one biquad per band to keep the phase behaviour sane.
    /// </summary>
    public sealed class Equalizer30BandEffect : IEffectProcessor
    {
        private const int BANDS = 30;
        private const int FILTERS_PER_BAND = 1;
        private const int TOTAL_FILTERS = BANDS * FILTERS_PER_BAND;

        /// <summary>
        /// Biquad coefficients per filter, a0 already normalized out.
        /// </summary>
        private readonly float[] _b0;
        private readonly float[] _b1;
        private readonly float[] _b2;
        private readonly float[] _a1;
        private readonly float[] _a2;

        /// <summary>
        /// Transposed DF2 state, one row per channel.
        /// </summary>
        private float[][] _z1;
        private float[][] _z2;

        private readonly float[] _gains;
        private readonly float[] _frequencies;
        private readonly float[] _qFactors;
        private float _sampleRate;

        /// <summary>
        /// Indices of the bands that actually do something, plus how many of them there are.
        /// </summary>
        private readonly int[] _activeBands;
        private int _activeCount;

        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig? _config;

        private static readonly float[] StandardFrequencies = {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
        };

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
        /// EQ has no dry path, this stays at 1.0.
        /// </summary>
        public float Mix { get; set; } = 1.0f;

        /// <summary>
        /// Builds the EQ. The optional array holds 30 band gains in dB.
        /// </summary>
        public Equalizer30BandEffect(float sampleRate = 44100, float[]? gains = null)
        {
            _id = Guid.NewGuid();
            _name = "Equalizer30Band";
            _enabled = true;
            _sampleRate = sampleRate;

            _b0 = new float[TOTAL_FILTERS];
            _b1 = new float[TOTAL_FILTERS];
            _b2 = new float[TOTAL_FILTERS];
            _a1 = new float[TOTAL_FILTERS];
            _a2 = new float[TOTAL_FILTERS];

            _z1 = new float[2][];
            _z2 = new float[2][];
            for (int i = 0; i < 2; i++)
            {
                _z1[i] = new float[TOTAL_FILTERS];
                _z2[i] = new float[TOTAL_FILTERS];
            }

            _gains = new float[BANDS];
            _frequencies = new float[BANDS];
            _qFactors = new float[BANDS];
            _activeBands = new int[BANDS];

            _initFilters();

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

        /// <summary>
        /// Takes the engine config, retunes on a rate change and resizes the per channel state.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
            {
                _sampleRate = config.SampleRate;
                for(int i = 0; i < BANDS; i++) _updateFilter(i);
            }

            int ch = config.Channels;
            if (_z1.Length != ch)
            {
                _z1 = new float[ch][];
                _z2 = new float[ch][];
                for(int i = 0; i < ch; i++)
                {
                    _z1[i] = new float[TOTAL_FILTERS];
                    _z2[i] = new float[TOTAL_FILTERS];
                }
            }
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
        /// Sets the ISO centres and the per band Q, everything flat to start with.
        /// </summary>
        private void _initFilters()
        {
            float[] _q = {
                0.6f, 0.6f, 0.7f, 0.7f, 0.8f, 0.8f, 0.9f, 1.0f, 1.0f, 1.1f,
                1.2f, 1.2f, 1.1f, 1.0f, 1.0f, 1.0f, 1.1f, 1.2f, 1.2f, 1.3f,
                1.4f, 1.3f, 1.2f, 1.1f, 1.0f, 0.9f, 0.8f, 0.7f, 0.7f, 0.6f
            };

            for (int band = 0; band < BANDS; band++)
            {
                _frequencies[band] = StandardFrequencies[band];
                _qFactors[band] = _q[band];
                _gains[band] = 0.0f;
                _updateFilter(band);
            }
        }

        /// <summary>
        /// RBJ peaking coefficients for one band.
        /// </summary>
        private void _updateFilter(int band)
        {
            float freq = _frequencies[band];
            float q = _qFactors[band];

            float omega = 2.0f * MathF.PI * freq / _sampleRate;
            float sinOmega = MathF.Sin(omega);
            float cosOmega = MathF.Cos(omega);
            float alpha = sinOmega / (2.0f * q);
            float A = MathF.Pow(10.0f, _gains[band] / 40.0f);

            float invA0 = 1.0f / (1.0f + alpha / A);

            int f = band * FILTERS_PER_BAND;
            _b0[f] = (1.0f + alpha * A) * invA0;
            _b1[f] = -2.0f * cosOmega * invA0;
            _b2[f] = (1.0f - alpha * A) * invA0;
            _a1[f] = _b1[f];
            _a2[f] = (1.0f - alpha / A) * invA0;
        }

        /// <summary>
        /// Collects the bands whose gain is not zero, so Process can skip the rest.
        /// </summary>
        private void _rebuildActive()
        {
            _activeCount = 0;
            for (int i = 0; i < BANDS; i++)
            {
                if (Math.Abs(_gains[i]) > 0.01f) _activeBands[_activeCount++] = i;
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

            if (Math.Abs(_gains[band] - gainDB) <= 0.001f &&
                Math.Abs(_frequencies[band] - frequency) <= 0.001f &&
                Math.Abs(_qFactors[band] - q) <= 0.001f) return;

            _frequencies[band] = frequency;
            _qFactors[band] = q;
            _gains[band] = gainDB;
            _updateFilter(band);
            _rebuildActive();
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
                    _gainCurve(gains, new[] { (0, 6f), (4, 5f), (7, 4f), (9, 2f), (11, -1f), (14, 0f), (17, 0f), (22, 1f), (26, 2f), (29, 1f) }); break;
                case Equalizer30Preset.Treble:
                    _gainCurve(gains, new[] { (0, 0f), (9, 0f), (14, 0f), (17, 1f), (20, 2f), (22, 4f), (25, 4f), (27, 5f), (29, 3f) }); break;
                case Equalizer30Preset.Rock:
                    _gainCurve(gains, new[] { (0, 5f), (3, 4f), (7, 2f), (11, -2f), (14, -2f), (17, 0f), (20, 2f), (22, 4f), (25, 4f), (27, 3f), (29, 2f) }); break;
                case Equalizer30Preset.Classical:
                    _gainCurve(gains, new[] { (0, 1f), (7, 0f), (14, -1f), (17, 0f), (20, 1f), (23, 2f), (26, 2f), (29, 1f) }); break;
                case Equalizer30Preset.Pop:
                    _gainCurve(gains, new[] { (0, 2f), (5, 2f), (9, 1f), (13, 2f), (17, 3f), (20, 3f), (22, 3f), (25, 2f), (27, 2f), (29, 2f) }); break;
                case Equalizer30Preset.Jazz:
                    _gainCurve(gains, new[] { (0, 3f), (4, 3f), (8, 2f), (12, 2f), (17, 1f), (20, 0f), (23, 0f), (26, 0f), (29, -1f) }); break;
                case Equalizer30Preset.Voice:
                    _gainCurve(gains, new[] { (0, -3f), (3, -2f), (8, 0f), (11, 2f), (14, 4f), (17, 5f), (20, 5f), (22, 3f), (25, 1f), (27, -1f), (29, -2f) }); break;
                case Equalizer30Preset.Electronic:
                    _gainCurve(gains, new[] { (0, 6f), (2, 6f), (4, 4f), (7, 2f), (11, 0f), (15, -1f), (18, 1f), (22, 3f), (25, 5f), (27, 5f), (29, 4f) }); break;
                case Equalizer30Preset.Acoustic:
                    _gainCurve(gains, new[] { (0, 1f), (7, 1f), (10, 2f), (13, 1f), (17, 2f), (20, 2f), (23, 1f), (25, 1f), (27, 0f), (29, -1f) }); break;
            }
            SetAllGains(gains);
        }

        /// <summary>
        /// Fills the gain array by interpolating between the given band/gain key points.
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
        /// Runs the active bands over every channel, with a wide safety clip at the end.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> samples, int frameCount)
        {
            if (_config == null || !_enabled || _activeCount == 0) return;

            int channels = _config.Channels;
            int totalLen = frameCount * channels;

            for (int ch = 0; ch < channels; ch++)
            {
                float[] z1 = _z1[ch];
                float[] z2 = _z2[ch];

                for (int i = ch; i < totalLen; i += channels)
                {
                    float input = samples[i];

                    for (int b = 0; b < _activeCount; b++)
                    {
                        int f = _activeBands[b] * FILTERS_PER_BAND;

                        float output = _b0[f] * input + z1[f];
                        z1[f] = _b1[f] * input - _a1[f] * output + z2[f];
                        z2[f] = _b2[f] * input - _a2[f] * output;
                        input = output;
                    }

                    if (Math.Abs(input) > 1.5f) input = Math.Sign(input) * 1.5f;

                    samples[i] = input;
                }
            }
        }

        /// <summary>
        /// Clears the filter memory of every channel.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < _z1.Length; i++)
            {
                Array.Clear(_z1[i], 0, _z1[i].Length);
                Array.Clear(_z2[i], 0, _z2[i].Length);
            }
        }

        /// <summary>
        /// Nothing unmanaged here.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            Reset();
            _disposed = true;
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString() => $"Equalizer30Band [ID: {_id}, Enabled: {_enabled}]";
    }
}
