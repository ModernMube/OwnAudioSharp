using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Equalizer preset enumeration for common audio enhancement scenarios.
    /// </summary>
    public enum Equalizer30Preset
    {
        /// <summary>
        /// Flat response with no gain adjustments.
        /// </summary>
        Default,
        
        /// <summary>
        /// Enhanced low-frequency response.
        /// </summary>
        Bass,
        
        /// <summary>
        /// Enhanced high-frequency response.
        /// </summary>
        Treble,
        
        /// <summary>
        /// Rock music optimization with enhanced bass and treble.
        /// </summary>
        Rock,
        
        /// <summary>
        /// Classical music optimization with natural dynamics.
        /// </summary>
        Classical,
        
        /// <summary>
        /// Pop music optimization with vocal clarity.
        /// </summary>
        Pop,
        
        /// <summary>
        /// Jazz music optimization with balanced response.
        /// </summary>
        Jazz,
        
        /// <summary>
        /// Voice optimization with enhanced mid-range.
        /// </summary>
        Voice,
        
        /// <summary>
        /// Electronic music optimization with deep bass and bright highs.
        /// </summary>
        Electronic,
        
        /// <summary>
        /// Acoustic music optimization with natural sound.
        /// </summary>
        Acoustic
    }

    /// <summary>
    /// Professional 30-band parametric equalizer
    /// Optimized with flattened Biquad structures and MathF for maximum performance
    /// </summary>
    public sealed class Equalizer30BandEffect : IEffectProcessor
    {
        // DSP Constants
        private const int BANDS = 30;
        private const int FILTERS_PER_BAND = 1; // Single filter per band to reduce phase distortion
        private const int TOTAL_FILTERS = BANDS * FILTERS_PER_BAND;

        // Flattened Filter Coefficients (Structure of Arrays) [FilterIndex] where Index = Band * FILTERS_PER_BAND
        private readonly float[] _b0;
        private readonly float[] _b1;
        private readonly float[] _b2;
        private readonly float[] _a1;
        private readonly float[] _a2;

        // Filter State (Per Channel) [Channel][FilterIndex]
        private float[][] _z1;
        private float[][] _z2;

        // Parameters
        private readonly float[] _gains;
        private readonly float[] _frequencies;
        private readonly float[] _qFactors;
        private float _sampleRate;
        
        // Active bands optimization - track which bands have non-zero gain
        private readonly List<int> _activeBands;

        // IEffectProcessor implementation
        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig? _config;

        // Professional 30-band frequencies
        private static readonly float[] StandardFrequencies = {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
        };

        /// <summary>
        /// Gets the unique identifier for this effect instance.
        /// </summary>
        public Guid Id => _id;
        
        /// <summary>
        /// Gets the name of this effect instance.
        /// </summary>
        public string Name => _name;
        
        /// <summary>
        /// Gets or sets whether this effect is enabled.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Gets or sets the wet/dry mix. Always 1.0 for Equalizer (no dry signal mixing).
        /// </summary>
        public float Mix { get; set; } = 1.0f;

        /// <summary>
        /// Initializes a new instance of the Equalizer30BandEffect with optional custom gains.
        /// </summary>
        /// <param name="sampleRate">Sample rate in Hz (default: 44100).</param>
        /// <param name="gains">Optional array of 30 gain values in dB.</param>
        public Equalizer30BandEffect(float sampleRate = 44100, float[]? gains = null)
        {
            _id = Guid.NewGuid();
            _name = "Equalizer30Band";
            _enabled = true;
            _sampleRate = sampleRate;

            // Allocations
            _b0 = new float[TOTAL_FILTERS];
            _b1 = new float[TOTAL_FILTERS];
            _b2 = new float[TOTAL_FILTERS];
            _a1 = new float[TOTAL_FILTERS];
            _a2 = new float[TOTAL_FILTERS];

            // Default capacity 2 channels
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
            
            // Initialize active bands list
            _activeBands = new List<int>(BANDS);

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
        /// Initializes a new instance of the Equalizer30BandEffect using a preset configuration.
        /// </summary>
        /// <param name="preset">The preset configuration to use.</param>
        /// <param name="sampleRate">Sample rate in Hz (default: 44100).</param>
        public Equalizer30BandEffect(Equalizer30Preset preset, float sampleRate = 44100) 
            : this(sampleRate)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Initializes the effect with the specified audio configuration.
        /// </summary>
        /// <param name="config">The audio configuration.</param>
        /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
            {
                _sampleRate = config.SampleRate;
                // Re-calc all filters for new rate
                for(int i=0; i<BANDS; i++)
                    UpdateFilter(i);
            }

            // Resize state if channels differ
            int ch = config.Channels;
            if (_z1.Length != ch)
            {
                _z1 = new float[ch][];
                _z2 = new float[ch][];
                for(int i=0; i<ch; i++)
                {
                    _z1[i] = new float[TOTAL_FILTERS];
                    _z2[i] = new float[TOTAL_FILTERS];
                }
            }
        }

        #region Band Properties

        /// <summary>
        /// Gets or sets the gain for a specific band by index.
        /// </summary>
        /// <param name="band">Band index (0-29).</param>
        /// <returns>Gain value in dB.</returns>
        public float this[int band]
        {
            get => GetBandGain(band);
            set => SetBandGain(band, StandardFrequencies[band], _qFactors[band], value);
        }

        /// <summary>
        /// Gets the gain value for a specific band.
        /// </summary>
        /// <param name="band">Band index (0-29).</param>
        /// <returns>Gain value in dB, or 0 if band index is invalid.</returns>
        public float GetBandGain(int band)
        {
            if (band < 0 || band >= BANDS) return 0f;
            return _gains[band];
        }

        /// <summary>
        /// Gets the center frequency for a specific band.
        /// </summary>
        /// <param name="band">Band index (0-29).</param>
        /// <returns>Frequency in Hz, or 0 if band index is invalid.</returns>
        public float GetBandFrequency(int band)
        {
            if (band < 0 || band >= BANDS) return 0f;
            return _frequencies[band];
        }

        /// <summary>
        /// Gets a copy of all band gain values.
        /// </summary>
        /// <returns>Array of 30 gain values in dB.</returns>
        public float[] GetAllGains()
        {
            float[] result = new float[BANDS];
            Array.Copy(_gains, result, BANDS);
            return result;
        }

        /// <summary>
        /// Sets all band gains from an array.
        /// </summary>
        /// <param name="gains">Array of at least 30 gain values in dB.</param>
        public void SetAllGains(float[] gains)
        {
            if (gains == null || gains.Length < BANDS) return;
            for (int i = 0; i < BANDS; i++)
            {
                SetBandGain(i, StandardFrequencies[i], _qFactors[i], gains[i]);
            }
        }

        /// <summary>
        /// Gets the current sample rate.
        /// </summary>
        public float SampleRate => _sampleRate;

        #endregion

        private void InitializeFilters()
        {
            // Optimized Q factors
            float[] optimizedQ = {
                0.6f, 0.6f, 0.7f, 0.7f, 0.8f, 0.8f, 0.9f, 1.0f, 1.0f, 1.1f,
                1.2f, 1.2f, 1.1f, 1.0f, 1.0f, 1.0f, 1.1f, 1.2f, 1.2f, 1.3f,
                1.4f, 1.3f, 1.2f, 1.1f, 1.0f, 0.9f, 0.8f, 0.7f, 0.7f, 0.6f
            };

            for (int band = 0; band < BANDS; band++)
            {
                _frequencies[band] = StandardFrequencies[band];
                _qFactors[band] = optimizedQ[band];
                _gains[band] = 0.0f;
                UpdateFilter(band);
            }
        }

        private void UpdateFilter(int band)
        {
            float freq = _frequencies[band];
            float q = _qFactors[band];
            float gain = _gains[band]; // Total Gain dB (now applied to single filter)

            // Single filter - full gain applied (reduces phase distortion for more natural sound)

            // Peaking EQ coeffs
            float omega = 2.0f * MathF.PI * freq / _sampleRate;
            float sinOmega = MathF.Sin(omega);
            float cosOmega = MathF.Cos(omega);
            float alpha = sinOmega / (2.0f * q);
            float A = MathF.Pow(10.0f, gain / 40.0f); // Full gain applied to single filter

            // Biquad peaking
            float b0 = 1.0f + alpha * A;
            float b1 = -2.0f * cosOmega;
            float b2 = 1.0f - alpha * A;
            float a0 = 1.0f + alpha / A;
            float a1 = -2.0f * cosOmega;
            float a2 = 1.0f - alpha / A;

            // Normalize
            float invA0 = 1.0f / a0;
            float fb0 = b0 * invA0;
            float fb1 = b1 * invA0;
            float fb2 = b2 * invA0;
            float fa1 = a1 * invA0;
            float fa2 = a2 * invA0;

            // Store in flat arrays (single filter per band now)
            int baseIdx = band * FILTERS_PER_BAND;
            
            // Single filter
            _b0[baseIdx] = fb0; _b1[baseIdx] = fb1; _b2[baseIdx] = fb2;
            _a1[baseIdx] = fa1; _a2[baseIdx] = fa2;
        }

        /// <summary>
        /// Sets the gain, frequency, and Q factor for a specific band.
        /// </summary>
        /// <param name="band">Band index (0-29).</param>
        /// <param name="frequency">Center frequency in Hz (20-20000).</param>
        /// <param name="q">Q factor (0.1-10.0).</param>
        /// <param name="gainDB">Gain in dB (-18 to +18).</param>
        public void SetBandGain(int band, float frequency, float q, float gainDB)
        {
            if (band < 0 || band >= BANDS) return;

            // Clamp params - Extended range for aggressive matching
            frequency = Math.Clamp(frequency, 20.0f, 20000.0f);
            q = Math.Clamp(q, 0.1f, 10.0f);
            gainDB = Math.Clamp(gainDB, -18f, 18f); // Extended range for aggressive EQ correction

            // Check diff
            if (Math.Abs(_gains[band] - gainDB) > 0.001f ||
                Math.Abs(_frequencies[band] - frequency) > 0.001f ||
                Math.Abs(_qFactors[band] - q) > 0.001f)
            {
                _frequencies[band] = frequency;
                _qFactors[band] = q;
                _gains[band] = gainDB;
                UpdateFilter(band);
                
                // Update active bands list
                bool isActive = Math.Abs(gainDB) > 0.01f;
                bool wasActive = _activeBands.Contains(band);
                
                if (isActive && !wasActive)
                {
                    _activeBands.Add(band);
                }
                else if (!isActive && wasActive)
                {
                    _activeBands.Remove(band);
                }
            }
        }

        /// <summary>
        /// Applies a preset configuration to the equalizer.
        /// </summary>
        /// <param name="preset">The preset to apply.</param>
        public void SetPreset(Equalizer30Preset preset)
        {
            float[] gains = new float[BANDS];
            switch (preset)
            {
                case Equalizer30Preset.Default: break;
                case Equalizer30Preset.Bass:
                    ApplyGainCurve(gains, new[] { (0, 7f), (5, 5f), (8, 3f), (10, 1f), (12, -1f), (15, 0f), (20, 1f), (25, 2f), (29, 1f) }); break;
                case Equalizer30Preset.Treble:
                    ApplyGainCurve(gains, new[] { (0, 0f), (10, 0f), (15, 1f), (18, 2f), (22, 4f), (25, 3f), (27, 4f), (29, 3f) }); break;
                case Equalizer30Preset.Rock:
                    ApplyGainCurve(gains, new[] { (0, 5f), (3, 4f), (8, 2f), (12, -2f), (15, -1f), (18, 2f), (22, 4f), (25, 3f), (29, 2f) }); break;
                case Equalizer30Preset.Classical:
                    ApplyGainCurve(gains, new[] { (0, 1f), (8, 0f), (15, 0f), (20, 1f), (25, 2f), (27, 2f), (29, 1f) }); break;
                case Equalizer30Preset.Pop:
                    ApplyGainCurve(gains, new[] { (0, 2f), (5, 1f), (12, 1f), (17, 3f), (20, 2f), (25, 2f), (29, 1f) }); break;
                case Equalizer30Preset.Jazz:
                    ApplyGainCurve(gains, new[] { (0, 2f), (5, 1f), (10, 1f), (17, 1f), (22, 0f), (27, 0f), (29, -1f) }); break;
                case Equalizer30Preset.Voice:
                    ApplyGainCurve(gains, new[] { (0, -3f), (3, -2f), (10, 1f), (15, 3f), (18, 4f), (22, 2f), (27, -1f), (29, -2f) }); break;
                case Equalizer30Preset.Electronic:
                    ApplyGainCurve(gains, new[] { (0, 8f), (2, 6f), (5, 3f), (10, 0f), (15, -1f), (18, 1f), (22, 3f), (26, 5f), (29, 4f) }); break;
                case Equalizer30Preset.Acoustic:
                    ApplyGainCurve(gains, new[] { (0, 1f), (8, 1f), (12, 0f), (17, 2f), (20, 1f), (24, 1f), (27, 0f), (29, -1f) }); break;
            }
            SetAllGains(gains);
        }

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
        /// Processes the audio buffer with equalization.
        /// </summary>
        /// <param name="samples">The audio buffer to process.</param>
        /// <param name="frameCount">The number of frames in the buffer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> samples, int frameCount)
        {
            if (_config == null || !_enabled) return;
            
            // Early exit if no active bands
            if (_activeBands.Count == 0) return;

            int channels = _config.Channels;
            int totalSamples = frameCount * channels;

            for (int ch = 0; ch < channels; ch++)
            {
                float[] z1 = _z1[ch];
                float[] z2 = _z2[ch];

                int stride = channels;
                for (int i = ch; i < totalSamples; i += stride)
                {
                    float input = samples[i];
                    
                    // Filter Chain - only process active bands
                    foreach (int band in _activeBands)
                    {
                        int f = band * FILTERS_PER_BAND;
                        
                        // Direct Form II Transposed
                        float output = _b0[f] * input + z1[f];
                        z1[f] = _b1[f] * input - _a1[f] * output + z2[f];
                        z2[f] = _b2[f] * input - _a2[f] * output;
                        input = output;
                    }

                    // Hard clip prevention only (let downstream limiter handle peaks)
                    if (Math.Abs(input) > 1.5f)
                    {
                        input = Math.Sign(input) * 1.5f; // Safety hard clip only
                    }

                    samples[i] = input;
                }
            }
        }

        /// <summary>
        /// Resets the filter state to initial values.
        /// </summary>
        public void Reset()
        {
            if (_z1 == null) return;
            for (int i = 0; i < _z1.Length; i++)
            {
                Array.Clear(_z1[i], 0, _z1[i].Length);
                Array.Clear(_z2[i], 0, _z2[i].Length);
            }
        }

        /// <summary>
        /// Disposes the effect and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            Reset();
            _disposed = true;
        }

        /// <summary>
        /// Returns a string representation of the effect's current state.
        /// </summary>
        /// <returns>A string describing the effect state.</returns>
        public override string ToString() => $"Equalizer30Band [ID: {_id}, Enabled: {_enabled}]";
    }
}
