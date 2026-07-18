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
    public sealed class EqualizerEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private AudioConfig? _config;

        private const int Bands = 10;
        private const int FiltersPerBand = 2;

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

        private readonly float[] _frequencies;
        private readonly float[] _gains;
        private readonly float[] _qFactors;
        private float _sampleRate = 44100;

        /// <summary>
        /// Indices of the bands that actually do something, plus how many of them there are.
        /// </summary>
        private readonly int[] _activeBands;
        private int _activeCount;

        private static readonly float[] StandardFrequencies = {
            31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f
        };

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "Equalizer"; }

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// EQ has no dry path, this stays at 1.0.
        /// </summary>
        public float Mix { get; set; } = 1.0f;

        /// <summary>
        /// Builds the EQ, band gains are in dB on the ISO centres from 31.25Hz up to 16kHz.
        /// </summary>
        public EqualizerEffect(float sampleRate = 44100,
                        float band0Gain = 0.0f, float band1Gain = 0.0f, float band2Gain = 0.0f, float band3Gain = 0.0f, float band4Gain = 0.0f,
                        float band5Gain = 0.0f, float band6Gain = 0.0f, float band7Gain = 0.0f, float band8Gain = 0.0f, float band9Gain = 0.0f)
        {
            _id = Guid.NewGuid();
            _name = "Equalizer";
            _enabled = true;
            _sampleRate = sampleRate;

            int totalFilters = Bands * FiltersPerBand;

            _b0 = new float[totalFilters];
            _b1 = new float[totalFilters];
            _b2 = new float[totalFilters];
            _a1 = new float[totalFilters];
            _a2 = new float[totalFilters];

            _z1 = new float[2][];
            _z2 = new float[2][];
            for(int c = 0; c < 2; c++)
            {
                _z1[c] = new float[totalFilters];
                _z2[c] = new float[totalFilters];
            }

            _frequencies = new float[Bands];
            _gains = new float[Bands];
            _qFactors = new float[Bands];
            _activeBands = new int[Bands];

            for(int i = 0; i < Bands; i++)
            {
                _frequencies[i] = StandardFrequencies[i];
                _qFactors[i] = 1.0f;
                _gains[i] = 0.0f;
            }

            _gains[0] = band0Gain; _gains[1] = band1Gain; _gains[2] = band2Gain; _gains[3] = band3Gain; _gains[4] = band4Gain;
            _gains[5] = band5Gain; _gains[6] = band6Gain; _gains[7] = band7Gain; _gains[8] = band8Gain; _gains[9] = band9Gain;

            _recalcAll();
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

        /// <summary>
        /// Takes the engine config, retunes on a rate change and resizes the per channel state.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
            {
                _sampleRate = config.SampleRate;
                _recalcAll();
            }

            int channels = config.Channels;
            if (_z1.Length != channels)
            {
                _z1 = new float[channels][];
                _z2 = new float[channels][];
                int totalFilters = Bands * FiltersPerBand;
                for(int c = 0; c < channels; c++)
                {
                    _z1[c] = new float[totalFilters];
                    _z2[c] = new float[totalFilters];
                }
            }
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
        /// Sets one band gain in dB and rebuilds its filter.
        /// </summary>
        private void _setBand(int index, float gain)
        {
            if (index < 0 || index >= Bands) return;
            if (Math.Abs(_gains[index] - gain) <= 0.01f) return;

            _gains[index] = Math.Clamp(gain, -12f, 12f);
            _updateFilter(index);
            _rebuildActive();
        }

        /// <summary>
        /// Retunes one band completely: centre frequency, Q and gain in dB.
        /// </summary>
        public void SetBandGain(int band, float frequency, float q, float gainDB)
        {
            if (band < 0 || band >= Bands) return;

            _frequencies[band] = Math.Clamp(frequency, 20f, 20000f);
            _qFactors[band] = Math.Clamp(q, 0.1f, 10f);
            _setBand(band, gainDB);
        }

        /// <summary>
        /// Rebuilds every band and the active list.
        /// </summary>
        private void _recalcAll()
        {
            for(int i = 0; i < Bands; i++) _updateFilter(i);
            _rebuildActive();
        }

        /// <summary>
        /// Collects the bands whose gain is not zero, so Process can skip the rest.
        /// </summary>
        private void _rebuildActive()
        {
            _activeCount = 0;
            for (int i = 0; i < Bands; i++)
            {
                if (Math.Abs(_gains[i]) > 0.01f) _activeBands[_activeCount++] = i;
            }
        }

        /// <summary>
        /// RBJ peaking coefficients for one band. Gain is halved because the two
        /// filters of the band are cascaded.
        /// </summary>
        private void _updateFilter(int bandIndex)
        {
            float freq = _frequencies[bandIndex];
            float q = _qFactors[bandIndex];
            float halfGain = _gains[bandIndex] * 0.5f;

            float omega = 2.0f * MathF.PI * freq / _sampleRate;
            float sinOmega = MathF.Sin(omega);
            float cosOmega = MathF.Cos(omega);
            float alpha = sinOmega / (2.0f * q);
            float A = MathF.Pow(10.0f, halfGain / 40.0f);

            float invA0 = 1.0f / (1.0f + alpha / A);

            float fb0 = (1.0f + alpha * A) * invA0;
            float fb1 = -2.0f * cosOmega * invA0;
            float fb2 = (1.0f - alpha * A) * invA0;
            float fa1 = fb1;
            float fa2 = (1.0f - alpha / A) * invA0;

            int baseIdx = bandIndex * FiltersPerBand;

            _b0[baseIdx] = fb0; _b1[baseIdx] = fb1; _b2[baseIdx] = fb2;
            _a1[baseIdx] = fa1; _a2[baseIdx] = fa2;

            _b0[baseIdx+1] = fb0; _b1[baseIdx+1] = fb1; _b2[baseIdx+1] = fb2;
            _a1[baseIdx+1] = fa1; _a2[baseIdx+1] = fa2;
        }

        /// <summary>
        /// Runs the active bands over every channel, then soft limits the boosted peaks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled || _activeCount == 0 || Mix < 0.01f) return;

            int channels = _config.Channels;
            int samples = frameCount * channels;
            float mix = Mix;

            for (int ch = 0; ch < channels; ch++)
            {
                float[] z1 = _z1[ch];
                float[] z2 = _z2[ch];

                for (int i = ch; i < samples; i += channels)
                {
                    float dry = buffer[i];
                    float sample = dry;

                    for (int b = 0; b < _activeCount; b++)
                    {
                        int f = _activeBands[b] * FiltersPerBand;

                        float output = _b0[f] * sample + z1[f];
                        z1[f] = _b1[f] * sample - _a1[f] * output + z2[f];
                        z2[f] = _b2[f] * sample - _a2[f] * output;
                        sample = output;

                        f++;
                        output = _b0[f] * sample + z1[f];
                        z1[f] = _b1[f] * sample - _a1[f] * output + z2[f];
                        z2[f] = _b2[f] * sample - _a2[f] * output;
                        sample = output;
                    }

                    if (Math.Abs(sample) > 0.95f) sample = 0.95f * MathF.Tanh(sample);

                    buffer[i] = dry * (1.0f - mix) + sample * mix;
                }
            }
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
        /// Drops in a whole curve at once and rebuilds everything.
        /// </summary>
        private void _setGains(float g0, float g1, float g2, float g3, float g4, float g5, float g6, float g7, float g8, float g9)
        {
            _gains[0] = g0; _gains[1] = g1; _gains[2] = g2; _gains[3] = g3; _gains[4] = g4;
            _gains[5] = g5; _gains[6] = g6; _gains[7] = g7; _gains[8] = g8; _gains[9] = g9;
            _recalcAll();
        }

        /// <summary>
        /// Clears the filter memory of every channel.
        /// </summary>
        public void Reset()
        {
            for(int c = 0; c < _z1.Length; c++)
            {
                Array.Clear(_z1[c], 0, _z1[c].Length);
                Array.Clear(_z2[c], 0, _z2[c].Length);
            }
        }

        /// <summary>
        /// Nothing to release.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString() => $"Equalizer: Enabled={_enabled}";
    }
}
