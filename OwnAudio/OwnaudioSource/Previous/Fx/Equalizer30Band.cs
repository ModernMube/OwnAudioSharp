using Ownaudio.Processors;
using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Equalizer preset enumeration for common audio enhancement scenarios
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public enum Equalizer30Preset
    {
        /// <summary>
        /// Default flat response - no EQ applied
        /// </summary>
        Default,

        /// <summary>
        /// Enhanced bass response for modern music
        /// </summary>
        Bass,

        /// <summary>
        /// Enhanced treble for clarity and detail
        /// </summary>
        Treble,

        /// <summary>
        /// Rock music optimization with mid-range presence
        /// </summary>
        Rock,

        /// <summary>
        /// Classical music with natural frequency response
        /// </summary>
        Classical,

        /// <summary>
        /// Pop music with enhanced vocals and presence
        /// </summary>
        Pop,

        /// <summary>
        /// Jazz optimization with smooth mid-range
        /// </summary>
        Jazz,

        /// <summary>
        /// Voice clarity enhancement for podcasts and speech
        /// </summary>
        Voice,

        /// <summary>
        /// Electronic music with enhanced sub-bass and highs
        /// </summary>
        Electronic,

        /// <summary>
        /// Acoustic music with warm, natural tones
        /// </summary>
        Acoustic
    }

    /// <summary>
    /// Professional 30-band parametric equalizer with preset configurations
    /// Features cascaded biquad filters for superior audio quality and phase response
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class Equalizer30Band : SampleProcessorBase
    {
        private readonly BiquadFilter[][] _filters;
        private readonly float[] _gains;
        private readonly float[] _frequencies;
        private readonly float[] _qFactors;
        private readonly float _sampleRate;
        private const int BANDS = 30;
        private const int FILTERS_PER_BAND = 2;

        // Professional 30-band frequencies for precise control
        private static readonly float[] StandardFrequencies = {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
        };

        /// <summary>
        /// Initializes a new instance of the 30-band Equalizer with all parameters
        /// </summary>
        /// <param name="sampleRate">Audio sample rate in Hz (typically 44100 or 48000)</param>
        /// <param name="gains">Array of 30 gain values in dB (-12 to +12), or null for flat response</param>
        public Equalizer30Band(float sampleRate = 44100, float[] gains = null)
        {
            _sampleRate = sampleRate;
            _gains = new float[BANDS];
            _frequencies = new float[BANDS];
            _qFactors = new float[BANDS];
            _filters = new BiquadFilter[BANDS][];

            InitializeFilters();

            if (gains != null && gains.Length >= BANDS)
            {
                for (int i = 0; i < BANDS; i++)
                {
                    SetBandGain(i, StandardFrequencies[i], _qFactors[i], gains[i]);
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the 30-band Equalizer with a preset configuration
        /// </summary>
        /// <param name="preset">The preset configuration to apply</param>
        /// <param name="sampleRate">Audio sample rate in Hz (typically 44100 or 48000)</param>
        public Equalizer30Band(Equalizer30Preset preset, float sampleRate = 44100)
        {
            _sampleRate = sampleRate;
            _gains = new float[BANDS];
            _frequencies = new float[BANDS];
            _qFactors = new float[BANDS];
            _filters = new BiquadFilter[BANDS][];

            InitializeFilters();
            SetPreset(preset);
        }

        #region Band Properties - Individual band access

        /// <summary>
        /// Gets or sets the gain for a specific band
        /// </summary>
        /// <param name="band">Band index (0-29)</param>
        /// <returns>Gain in dB</returns>
        public float this[int band]
        {
            get => GetBandGain(band);
            set => SetBandGain(band, StandardFrequencies[band], _qFactors[band], value);
        }

        /// <summary>
        /// Gets the gain for a specific band
        /// </summary>
        /// <param name="band">Band index (0-29)</param>
        /// <returns>Gain in dB</returns>
        public float GetBandGain(int band)
        {
            if (band < 0 || band >= BANDS)
                throw new ArgumentOutOfRangeException(nameof(band), "Band index must be between 0 and 29");
            return _gains[band];
        }

        /// <summary>
        /// Gets the frequency for a specific band
        /// </summary>
        /// <param name="band">Band index (0-29)</param>
        /// <returns>Frequency in Hz</returns>
        public float GetBandFrequency(int band)
        {
            if (band < 0 || band >= BANDS)
                throw new ArgumentOutOfRangeException(nameof(band), "Band index must be between 0 and 29");
            return _frequencies[band];
        }

        /// <summary>
        /// Gets all current gain values
        /// </summary>
        /// <returns>Array of 30 gain values in dB</returns>
        public float[] GetAllGains()
        {
            float[] result = new float[BANDS];
            Array.Copy(_gains, result, BANDS);
            return result;
        }

        /// <summary>
        /// Sets all band gains at once
        /// </summary>
        /// <param name="gains">Array of 30 gain values in dB</param>
        public void SetAllGains(float[] gains)
        {
            if (gains == null || gains.Length < BANDS)
                throw new ArgumentException("Gains array must contain at least 30 values");

            for (int i = 0; i < BANDS; i++)
            {
                SetBandGain(i, StandardFrequencies[i], _qFactors[i], gains[i]);
            }
        }

        /// <summary>
        /// Gets or sets the sample rate for the equalizer
        /// </summary>
        public float SampleRate => _sampleRate;

        #endregion

        /// <summary>
        /// Initializes filter arrays and sets up default frequency bands
        /// </summary>
        private void InitializeFilters()
        {
            // Optimized Q factors for different frequency ranges
            float[] optimizedQ = {
                0.6f, 0.6f, 0.7f, 0.7f, 0.8f, 0.8f, 0.9f, 1.0f, 1.0f, 1.1f,  // Sub-bass to low-mid
                1.2f, 1.2f, 1.1f, 1.0f, 1.0f, 1.0f, 1.1f, 1.2f, 1.2f, 1.3f,  // Mid-range
                1.4f, 1.3f, 1.2f, 1.1f, 1.0f, 0.9f, 0.8f, 0.7f, 0.7f, 0.6f   // High frequencies
            };

            for (int band = 0; band < BANDS; band++)
            {
                _filters[band] = new BiquadFilter[FILTERS_PER_BAND];
                for (int i = 0; i < FILTERS_PER_BAND; i++)
                {
                    _filters[band][i] = new BiquadFilter();
                }

                _frequencies[band] = StandardFrequencies[band];
                _qFactors[band] = optimizedQ[band];
                _gains[band] = 0.0f;
            }
        }

        /// <summary>
        /// Updates filter coefficients for a specific band with current parameters
        /// </summary>
        /// <param name="band">Band index (0-29)</param>
        private void UpdateFilters(int band)
        {
            for (int i = 0; i < FILTERS_PER_BAND; i++)
            {
                _filters[band][i].SetPeakingEq(
                    _sampleRate,
                    _frequencies[band],
                    _qFactors[band],
                    _gains[band] * 0.5f
                );
            }
        }

        /// <summary>
        /// Configures EQ parameters for a specific frequency band
        /// </summary>
        /// <param name="band">Band index (0-29)</param>
        /// <param name="frequency">Center frequency in Hz (20-20000)</param>
        /// <param name="q">Quality factor (0.1-10.0)</param>
        /// <param name="gainDB">Gain adjustment in dB (-12 to +12)</param>
        public void SetBandGain(int band, float frequency, float q, float gainDB)
        {
            if (band < 0 || band >= BANDS)
                throw new ArgumentOutOfRangeException(nameof(band), "Band index must be between 0 and 29");

            frequency = Math.Max(20.0f, Math.Min(20000.0f, frequency));
            q = Math.Max(0.1f, Math.Min(10.0f, q));
            gainDB = FastClamp(gainDB);

            _frequencies[band] = frequency;
            _qFactors[band] = q;
            _gains[band] = gainDB;

            UpdateFilters(band);
        }

        /// <summary>
        /// Applies a predefined equalizer preset for common audio scenarios
        /// </summary>
        /// <param name="preset">The preset configuration to apply</param>
        public void SetPreset(Equalizer30Preset preset)
        {
            float[] gains = new float[BANDS];

            switch (preset)
            {
                case Equalizer30Preset.Default:
                    // Flat response
                    break;

                case Equalizer30Preset.Bass:
                    // Enhanced low-end response
                    ApplyGainCurve(gains, new[] {
                        (0, 7f), (5, 5f), (8, 3f), (10, 1f), (12, -1f), (15, 0f), (20, 1f), (25, 2f), (29, 1f)
                    });
                    break;

                case Equalizer30Preset.Treble:
                    // Enhanced high frequencies
                    ApplyGainCurve(gains, new[] {
                        (0, 0f), (10, 0f), (15, 1f), (18, 2f), (22, 4f), (25, 3f), (27, 4f), (29, 3f)
                    });
                    break;

                case Equalizer30Preset.Rock:
                    // V-shaped curve for rock music
                    ApplyGainCurve(gains, new[] {
                        (0, 5f), (3, 4f), (8, 2f), (12, -2f), (15, -1f), (18, 2f), (22, 4f), (25, 3f), (29, 2f)
                    });
                    break;

                case Equalizer30Preset.Classical:
                    // Natural, subtle enhancements
                    ApplyGainCurve(gains, new[] {
                        (0, 1f), (8, 0f), (15, 0f), (20, 1f), (25, 2f), (27, 2f), (29, 1f)
                    });
                    break;

                case Equalizer30Preset.Pop:
                    // Vocal presence and modern sound
                    ApplyGainCurve(gains, new[] {
                        (0, 2f), (5, 1f), (12, 1f), (17, 3f), (20, 2f), (25, 2f), (29, 1f)
                    });
                    break;

                case Equalizer30Preset.Jazz:
                    // Warm, smooth response
                    ApplyGainCurve(gains, new[] {
                        (0, 2f), (5, 1f), (10, 1f), (17, 1f), (22, 0f), (27, 0f), (29, -1f)
                    });
                    break;

                case Equalizer30Preset.Voice:
                    // Speech clarity optimization
                    ApplyGainCurve(gains, new[] {
                        (0, -3f), (3, -2f), (10, 1f), (15, 3f), (18, 4f), (22, 2f), (27, -1f), (29, -2f)
                    });
                    break;

                case Equalizer30Preset.Electronic:
                    // Sub-bass and high-end emphasis for EDM
                    ApplyGainCurve(gains, new[] {
                        (0, 8f), (2, 6f), (5, 3f), (10, 0f), (15, -1f), (18, 1f), (22, 3f), (26, 5f), (29, 4f)
                    });
                    break;

                case Equalizer30Preset.Acoustic:
                    // Warm, natural acoustic sound
                    ApplyGainCurve(gains, new[] {
                        (0, 1f), (8, 1f), (12, 0f), (17, 2f), (20, 1f), (24, 1f), (27, 0f), (29, -1f)
                    });
                    break;

                default:
                    break;
            }

            SetAllGains(gains);
        }

        /// <summary>
        /// Applies gain curve using interpolation between key points
        /// </summary>
        /// <param name="gains">Target gains array to fill</param>
        /// <param name="keyPoints">Array of (band, gain) tuples</param>
        private void ApplyGainCurve(float[] gains, (int band, float gain)[] keyPoints)
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
        /// Processes audio samples through the 30-band equalizer
        /// </summary>
        /// <param name="samples">Audio sample buffer to process in-place</param>
        public override void Process(Span<float> samples)
        {
            for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                float sample = samples[sampleIndex];

                for (int band = 0; band < BANDS; band++)
                {
                    if (Math.Abs(_gains[band]) > 0.01f)
                    {
                        sample = _filters[band][0].Process(sample);
                        sample = _filters[band][1].Process(sample);
                    }
                }

                samples[sampleIndex] = SoftLimit(sample);
            }
        }

        /// <summary>
        /// Resets all filter states while preserving current EQ settings
        /// </summary>
        public override void Reset()
        {
            for (int band = 0; band < BANDS; band++)
            {
                for (int filter = 0; filter < FILTERS_PER_BAND; filter++)
                {
                    _filters[band][filter].Reset();
                }
            }
        }

        /// <summary>
        /// Professional soft limiting function
        /// </summary>
        /// <param name="sample">Input audio sample</param>
        /// <returns>Soft-limited audio sample</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SoftLimit(float sample)
        {
            if (Math.Abs(sample) > 0.8f)
            {
                return Math.Sign(sample) * (0.8f + 0.2f * MathF.Tanh((Math.Abs(sample) - 0.8f) * 5.0f));
            }
            return sample;
        }

        /// <summary>
        /// Fast gain clamping for EQ parameter validation
        /// </summary>
        /// <param name="value">Gain value in dB</param>
        /// <returns>Clamped gain value between -12dB and +12dB</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastClamp(float value)
        {
            return value < -12.0f ? -12.0f : (value > 12.0f ? 12.0f : value);
        }
    }
}