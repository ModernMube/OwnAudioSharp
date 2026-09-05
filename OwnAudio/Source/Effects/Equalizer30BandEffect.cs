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

        private readonly float[] _gains;
        private readonly float[] _frequencies;
        private readonly float[] _qFactors;
        private float _sampleRate;

        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private readonly NativeEffectEngine _native = new NativeEffectEngine();
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
        /// Dry/wet. The native 30-band engine honours it; 0 is fully dry.
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

            _gains = new float[BANDS];
            _frequencies = new float[BANDS];
            _qFactors = new float[BANDS];

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
        /// Takes the engine config and follows its rate. The filtering itself is native.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _sampleRate = config.SampleRate;

            _native.Initialize(this, config);
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
        /// Q of one band. Internal: the shape reaches the engine through the mirror,
        /// the public way in is SetBandGain, and the api baseline stays put.
        /// </summary>
        internal float BandQAt(int band) => band < 0 || band >= BANDS ? BandQ : _qFactors[band];

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
        /// Constant-Q for 1/3-octave spacing. The old 0.6-1.4 table gave 1.4 octave
        /// wide bells, so a drawn curve summed to about 5x what you set.
        /// </summary>
        private const float BandQ = 4.318474f;

        /// <summary>
        /// ISO centres, constant Q, flat to start with.
        /// </summary>
        private void _initFilters()
        {
            for (int band = 0; band < BANDS; band++)
            {
                _frequencies[band] = StandardFrequencies[band];
                _qFactors[band] = BandQ;
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

            if (Math.Abs(_gains[band] - gainDB) <= 0.001f &&
                Math.Abs(_frequencies[band] - frequency) <= 0.001f &&
                Math.Abs(_qFactors[band] - q) <= 0.001f) return;

            _frequencies[band] = frequency;
            _qFactors[band] = q;
            _gains[band] = gainDB;
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
                    _gainCurve(gains, new[] { (0, 6f), (3, 5.5f), (5, 4.5f), (7, 2.5f), (9, 1f), (11, -1f), (13, -1f), (16, 0f), (22, 0f), (26, 0.7f), (29, 0.5f) }); break;
                case Equalizer30Preset.Treble:
                    _gainCurve(gains, new[] { (0, 0f), (14, 0f), (17, 0f), (20, 1.2f), (23, 2.8f), (26, 4.5f), (29, 5f) }); break;
                case Equalizer30Preset.Rock:
                    _gainCurve(gains, new[] { (0, 4f), (3, 4f), (5, 3.5f), (7, 1.3f), (11, -2f), (14, -2f), (17, 0f), (21, 2f), (23, 3.3f), (26, 3.3f), (29, 2f) }); break;
                case Equalizer30Preset.Classical:
                    _gainCurve(gains, new[] { (0, 1.3f), (3, 1f), (7, 0f), (13, -0.7f), (17, 0f), (23, 1f), (27, 1.6f), (29, 1.3f) }); break;
                case Equalizer30Preset.Pop:
                    _gainCurve(gains, new[] { (0, 2f), (5, 2f), (9, 0.7f), (11, 0f), (16, 1.3f), (18, 1.6f), (21, 2f), (23, 2f), (25, 1.3f), (27, 2f), (29, 1.6f) }); break;
                case Equalizer30Preset.Jazz:
                    _gainCurve(gains, new[] { (0, 2f), (4, 2f), (8, 1.3f), (11, 1.3f), (17, 0f), (20, 0f), (25, -0.7f), (29, -1.3f) }); break;
                case Equalizer30Preset.Voice:
                    _gainCurve(gains, new[] { (0, -3.3f), (3, -2.6f), (7, -1.3f), (13, 1.3f), (17, 3.3f), (20, 3.3f), (22, 2f), (25, 0f), (29, -2f) }); break;
                case Equalizer30Preset.Electronic:
                    _gainCurve(gains, new[] { (0, 6f), (2, 6f), (4, 4f), (7, 2f), (11, 0f), (15, -0.7f), (18, 1.3f), (21, 2.6f), (25, 4f), (27, 4.2f), (29, 4.5f) }); break;
                case Equalizer30Preset.Acoustic:
                    _gainCurve(gains, new[] { (0, 1.3f), (7, 1f), (10, 1.3f), (13, 0.7f), (17, 1.6f), (20, 1.6f), (23, 1f), (25, 0.7f), (27, 0f), (29, -1f) }); break;
            }
            SetAllGains(gains);
        }

        /// <summary>
        /// Fills the gain array by interpolating between the given band/gain key points.
        /// A broad stretch of equal gains ends up about 1.6x what you write, since
        /// the neighbouring bells still overlap at constant Q - the curves above are
        /// scaled for that. Bands outside the first and last key point stay flat.
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
        /// Same DSP the mixer twin runs, on this instance's native handle.
        /// </summary>
        public void Process(Span<float> samples, int frameCount)
        {
            _native.Process(this, samples, frameCount);
        }

        /// <summary>
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        /// <summary>
        /// Clears the filter memory of every channel.
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
            Reset();
            _native.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString() => $"Equalizer30Band [ID: {_id}, Enabled: {_enabled}]";
    }
}
