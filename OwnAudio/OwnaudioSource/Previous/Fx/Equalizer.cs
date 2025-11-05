using OwnaudioLegacy.Processors;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioLegacy.Fx
{
    /// <summary>
    /// Equalizer preset enumeration for common audio enhancement scenarios
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public enum EqualizerPreset
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
        Voice
    }

    /// <summary>
    /// Professional 10-band parametric equalizer with preset configurations
    /// Features cascaded biquad filters for superior audio quality and phase response
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class Equalizer : SampleProcessorBase
    {
        private readonly BiquadFilter[][] _filters;
        private readonly float[] _gains;
        private readonly float[] _frequencies;
        private readonly float[] _qFactors;
        private readonly float _sampleRate;
        private const int BANDS = 10;
        private const int FILTERS_PER_BAND = 2;

        // Standard ISO frequencies for 10-band EQ
        private static readonly float[] StandardFrequencies = {
            31.25f,   // Sub-bass
            62.5f,    // Bass
            125f,     // Low-mid
            250f,     // Mid-bass
            500f,     // Mid
            1000f,    // Upper-mid
            2000f,    // Presence
            4000f,    // Brilliance
            8000f,    // Air
            16000f    // Ultra-high
        };

        /// <summary>
        /// Initializes a new instance of the Equalizer with all parameters and default values
        /// </summary>
        /// <param name="sampleRate">Audio sample rate in Hz (typically 44100 or 48000)</param>
        /// <param name="band0Gain">Band 0 (31.25Hz) gain in dB (-12 to +12)</param>
        /// <param name="band1Gain">Band 1 (62.5Hz) gain in dB (-12 to +12)</param>
        /// <param name="band2Gain">Band 2 (125Hz) gain in dB (-12 to +12)</param>
        /// <param name="band3Gain">Band 3 (250Hz) gain in dB (-12 to +12)</param>
        /// <param name="band4Gain">Band 4 (500Hz) gain in dB (-12 to +12)</param>
        /// <param name="band5Gain">Band 5 (1000Hz) gain in dB (-12 to +12)</param>
        /// <param name="band6Gain">Band 6 (2000Hz) gain in dB (-12 to +12)</param>
        /// <param name="band7Gain">Band 7 (4000Hz) gain in dB (-12 to +12)</param>
        /// <param name="band8Gain">Band 8 (8000Hz) gain in dB (-12 to +12)</param>
        /// <param name="band9Gain">Band 9 (16000Hz) gain in dB (-12 to +12)</param>
        public Equalizer(float sampleRate = 44100,
                        float band0Gain = 0.0f, float band1Gain = 0.0f, float band2Gain = 0.0f, float band3Gain = 0.0f, float band4Gain = 0.0f,
                        float band5Gain = 0.0f, float band6Gain = 0.0f, float band7Gain = 0.0f, float band8Gain = 0.0f, float band9Gain = 0.0f)
        {
            _sampleRate = sampleRate;
            _gains = new float[BANDS];
            _frequencies = new float[BANDS];
            _qFactors = new float[BANDS];
            _filters = new BiquadFilter[BANDS][];

            InitializeFilters();

            // Set individual band gains with validation
            Band0Gain = band0Gain;
            Band1Gain = band1Gain;
            Band2Gain = band2Gain;
            Band3Gain = band3Gain;
            Band4Gain = band4Gain;
            Band5Gain = band5Gain;
            Band6Gain = band6Gain;
            Band7Gain = band7Gain;
            Band8Gain = band8Gain;
            Band9Gain = band9Gain;
        }

        /// <summary>
        /// Initializes a new instance of the Equalizer with a preset configuration
        /// </summary>
        /// <param name="preset">The preset configuration to apply</param>
        /// <param name="sampleRate">Audio sample rate in Hz (typically 44100 or 48000)</param>
        public Equalizer(EqualizerPreset preset, float sampleRate = 44100)
        {
            _sampleRate = sampleRate;
            _gains = new float[BANDS];
            _frequencies = new float[BANDS];
            _qFactors = new float[BANDS];
            _filters = new BiquadFilter[BANDS][];

            InitializeFilters();
            SetPreset(preset);
        }

        #region Band Properties - Read/Write for real-time control

        /// <summary>
        /// Gets or sets the gain for band 0 (31.25Hz) in dB
        /// </summary>
        public float Band0Gain
        {
            get => _gains[0];
            set => SetBandGain(0, StandardFrequencies[0], _qFactors[0], value);
        }

        /// <summary>
        /// Gets or sets the gain for band 1 (62.5Hz) in dB
        /// </summary>
        public float Band1Gain
        {
            get => _gains[1];
            set => SetBandGain(1, StandardFrequencies[1], _qFactors[1], value);
        }

        /// <summary>
        /// Gets or sets the gain for band 2 (125Hz) in dB
        /// </summary>
        public float Band2Gain
        {
            get => _gains[2];
            set => SetBandGain(2, StandardFrequencies[2], _qFactors[2], value);
        }

        /// <summary>
        /// Gets or sets the gain for band 3 (250Hz) in dB
        /// </summary>
        public float Band3Gain
        {
            get => _gains[3];
            set => SetBandGain(3, StandardFrequencies[3], _qFactors[3], value);
        }

        /// <summary>
        /// Gets or sets the gain for band 4 (500Hz) in dB
        /// </summary>
        public float Band4Gain
        {
            get => _gains[4];
            set => SetBandGain(4, StandardFrequencies[4], _qFactors[4], value);
        }

        /// <summary>
        /// Gets or sets the gain for band 5 (1000Hz) in dB
        /// </summary>
        public float Band5Gain
        {
            get => _gains[5];
            set => SetBandGain(5, StandardFrequencies[5], _qFactors[5], value);
        }

        /// <summary>
        /// Gets or sets the gain for band 6 (2000Hz) in dB
        /// </summary>
        public float Band6Gain
        {
            get => _gains[6];
            set => SetBandGain(6, StandardFrequencies[6], _qFactors[6], value);
        }

        /// <summary>
        /// Gets or sets the gain for band 7 (4000Hz) in dB
        /// </summary>
        public float Band7Gain
        {
            get => _gains[7];
            set => SetBandGain(7, StandardFrequencies[7], _qFactors[7], value);
        }

        /// <summary>
        /// Gets or sets the gain for band 8 (8000Hz) in dB
        /// </summary>
        public float Band8Gain
        {
            get => _gains[8];
            set => SetBandGain(8, StandardFrequencies[8], _qFactors[8], value);
        }

        /// <summary>
        /// Gets or sets the gain for band 9 (16000Hz) in dB
        /// </summary>
        public float Band9Gain
        {
            get => _gains[9];
            set => SetBandGain(9, StandardFrequencies[9], _qFactors[9], value);
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
            for (int band = 0; band < BANDS; band++)
            {
                _filters[band] = new BiquadFilter[FILTERS_PER_BAND];
                for (int i = 0; i < FILTERS_PER_BAND; i++)
                {
                    _filters[band][i] = new BiquadFilter();
                }

                // Use standard ISO frequencies for professional EQ response
                _frequencies[band] = StandardFrequencies[band];
                _qFactors[band] = 1.0f;  // Moderate Q for musical response
                _gains[band] = 0.0f;     // Flat response initially
            }
        }

        /// <summary>
        /// Updates filter coefficients for a specific band with current parameters
        /// </summary>
        /// <param name="band">Band index (0-9)</param>
        private void UpdateFilters(int band)
        {
            for (int i = 0; i < FILTERS_PER_BAND; i++)
            {
                _filters[band][i].SetPeakingEq(
                    _sampleRate,
                    _frequencies[band],
                    _qFactors[band],
                    _gains[band] * 0.5f  // Split gain across cascaded filters for smoother response
                );
            }
        }

        /// <summary>
        /// Configures EQ parameters for a specific frequency band
        /// </summary>
        /// <param name="band">Band index (0-9)</param>
        /// <param name="frequency">Center frequency in Hz (20-20000)</param>
        /// <param name="q">Quality factor (0.1-10.0) - higher values create narrower bands</param>
        /// <param name="gainDB">Gain adjustment in dB (-12 to +12)</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when band index is invalid</exception>
        public void SetBandGain(int band, float frequency, float q, float gainDB)
        {
            if (band < 0 || band >= BANDS)
                throw new ArgumentOutOfRangeException(nameof(band), "Band index must be between 0 and 9");

            // Constrain parameters to safe audio ranges
            frequency = Math.Max(20.0f, Math.Min(20000.0f, frequency));
            q = Math.Max(0.1f, Math.Min(10.0f, q));
            gainDB = FastClamp(gainDB);

            // Store validated parameters
            _frequencies[band] = frequency;
            _qFactors[band] = q;
            _gains[band] = gainDB;

            UpdateFilters(band);
        }

        /// <summary>
        /// Applies a predefined equalizer preset for common audio scenarios
        /// </summary>
        /// <param name="preset">The preset configuration to apply</param>
        public void SetPreset(EqualizerPreset preset)
        {
            switch (preset)
            {
                case EqualizerPreset.Default:
                    // Flat response - no coloration, reference monitoring
                    ApplyPresetGains(new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                    break;

                case EqualizerPreset.Bass:
                    // Enhanced low-end for modern genres like hip-hop, EDM
                    // Boosts sub-bass and bass while slightly reducing mid-bass to prevent muddiness
                    ApplyPresetGains(new float[] { 6, 4, 2, -1, 0, 0, 0, 1, 2, 0 });
                    break;

                case EqualizerPreset.Treble:
                    // Brightens audio for detail and clarity, useful for older recordings
                    // Emphasizes presence and air frequencies while maintaining balance
                    ApplyPresetGains(new float[] { 0, 0, 0, 0, 1, 2, 4, 3, 4, 2 });
                    break;

                case EqualizerPreset.Rock:
                    // V-shaped curve ideal for rock/metal with powerful drums and clear vocals
                    // Boosts bass for punch, cuts lower mids to reduce muddiness, enhances presence
                    ApplyPresetGains(new float[] { 4, 3, 1, -2, -1, 1, 3, 2, 2, 1 });
                    break;

                case EqualizerPreset.Classical:
                    // Natural, musical response respecting original recording balance
                    // Subtle enhancements without artificial coloration
                    ApplyPresetGains(new float[] { 1, 0, 0, 0, 0, 0, 1, 1, 2, 2 });
                    break;

                case EqualizerPreset.Pop:
                    // Optimized for vocal clarity and modern production standards
                    // Enhances vocal presence while maintaining musical balance
                    ApplyPresetGains(new float[] { 2, 1, 0, 1, 2, 3, 2, 1, 2, 1 });
                    break;

                case EqualizerPreset.Jazz:
                    // Warm, smooth response emphasizing musical instruments
                    // Slight bass warmth, clear mids for instruments, controlled highs
                    ApplyPresetGains(new float[] { 2, 1, 1, 0, 0, 1, 0, 1, 0, -1 });
                    break;

                case EqualizerPreset.Voice:
                    // Speech intelligibility optimization for podcasts, audiobooks
                    // Reduces bass rumble, emphasizes vocal formant frequencies
                    ApplyPresetGains(new float[] { -3, -2, 0, 2, 4, 3, 2, 0, -1, -2 });
                    break;

                default:
                    ApplyPresetGains(new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                    break;
            }
        }

        /// <summary>
        /// Applies gain values to all bands with optimized Q factors for each frequency range
        /// </summary>
        /// <param name="gains">Array of 10 gain values in dB</param>
        private void ApplyPresetGains(float[] gains)
        {
            // Optimized Q factors for each frequency band based on psychoacoustic principles
            float[] optimizedQ = { 0.7f, 0.8f, 1.0f, 1.2f, 1.0f, 1.0f, 1.2f, 1.4f, 1.2f, 0.8f };

            for (int i = 0; i < BANDS; i++)
            {
                _gains[i] = FastClamp(gains[i]); // Validate gain values
                _qFactors[i] = optimizedQ[i];
                UpdateFilters(i);
            }
        }

        /// <summary>
        /// Processes audio samples through the equalizer using professional cascaded filtering
        /// </summary>
        /// <param name="samples">Audio sample buffer to process in-place</param>
        public override void Process(Span<float> samples)
        {
            for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                float sample = samples[sampleIndex];

                // Process through each frequency band
                for (int band = 0; band < BANDS; band++)
                {
                    // Skip processing if gain is effectively zero (performance optimization)
                    if (Math.Abs(_gains[band]) > 0.01f)
                    {
                        // Cascaded filtering for superior phase response and steeper roll-off
                        sample = _filters[band][0].Process(sample);
                        sample = _filters[band][1].Process(sample);
                    }
                }

                // Apply soft limiting to prevent clipping while preserving dynamics
                samples[sampleIndex] = SoftLimit(sample);
            }
        }

        /// <summary>
        /// Resets all filter states while preserving current EQ settings
        /// Call this when switching audio sources or after processing discontinuities
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
        /// Professional soft limiting function that prevents harsh clipping
        /// Uses hyperbolic tangent for smooth, musical saturation
        /// </summary>
        /// <param name="sample">Input audio sample</param>
        /// <returns>Soft-limited audio sample</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SoftLimit(float sample)
        {
            // Soft knee limiting starts at 0.8 to preserve dynamics
            if (Math.Abs(sample) > 0.8f)
            {
                return Math.Sign(sample) * (0.8f + 0.2f * MathF.Tanh((Math.Abs(sample) - 0.8f) * 5.0f));
            }
            return sample;
        }

        /// <summary>
        /// Fast gain clamping optimized for EQ parameter validation
        /// </summary>
        /// <param name="value">Gain value in dB</param>
        /// <returns>Clamped gain value between -12dB and +12dB</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastClamp(float value)
        {
            return value < -12.0f ? -12.0f : (value > 12.0f ? 12.0f : value);
        }
    }

    /// <summary>
    /// High-quality biquad filter implementation for parametric EQ applications
    /// Uses Direct Form II transposed structure for improved numerical stability
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class BiquadFilter
    {
        private float _a0, _a1, _a2, _b0, _b1, _b2;
        private float _z1, _z2;  // Direct Form II state variables for better numerical stability

        /// <summary>
        /// Gets the sample rate this filter was configured for
        /// </summary>
        public float SampleRate { get; private set; }

        /// <summary>
        /// Configures the biquad as a peaking equalizer with specified parameters
        /// Uses cookbook formulas for accurate frequency response
        /// </summary>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        /// <param name="centerFreq">Center frequency in Hz</param>
        /// <param name="q">Quality factor (bandwidth = centerFreq/Q)</param>
        /// <param name="gainDB">Peak gain in dB</param>
        public void SetPeakingEq(float sampleRate, float centerFreq, float q, float gainDB)
        {
            SampleRate = sampleRate;

            // Prevent numerical issues at extreme frequencies
            centerFreq = Math.Max(1.0f, Math.Min(sampleRate * 0.49f, centerFreq));

            float omega = 2.0f * MathF.PI * centerFreq / sampleRate;
            float sinOmega = MathF.Sin(omega);
            float cosOmega = MathF.Cos(omega);
            float alpha = sinOmega / (2.0f * q);
            float A = MathF.Pow(10.0f, gainDB / 40.0f);

            // Cookbook peaking EQ coefficients
            _b0 = 1.0f + alpha * A;
            _b1 = -2.0f * cosOmega;
            _b2 = 1.0f - alpha * A;
            _a0 = 1.0f + alpha / A;
            _a1 = -2.0f * cosOmega;
            _a2 = 1.0f - alpha / A;

            // Normalize coefficients for numerical stability
            float invA0 = 1.0f / _a0;
            _b0 *= invA0;
            _b1 *= invA0;
            _b2 *= invA0;
            _a1 *= invA0;
            _a2 *= invA0;
        }

        /// <summary>
        /// Processes a single audio sample through the biquad filter
        /// Uses Direct Form II transposed for optimal numerical precision
        /// </summary>
        /// <param name="input">Input audio sample</param>
        /// <returns>Filtered audio sample</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float input)
        {
            // Direct Form II transposed - more numerically stable
            float output = _b0 * input + _z1;
            _z1 = _b1 * input - _a1 * output + _z2;
            _z2 = _b2 * input - _a2 * output;

            return output;
        }

        /// <summary>
        /// Resets filter state variables to zero
        /// Call when starting new audio stream or after processing gaps
        /// </summary>
        public void Reset()
        {
            _z1 = 0.0f;
            _z2 = 0.0f;
        }
    }
}
