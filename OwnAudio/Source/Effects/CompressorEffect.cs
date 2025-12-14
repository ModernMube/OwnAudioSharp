using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Compression presets for different audio processing scenarios
    /// </summary>
    public enum CompressorPreset
    {
        /// <summary>
        /// Default preset with balanced settings suitable for general use
        /// </summary>
        Default,

        /// <summary>
        /// Gentle vocal compression - subtle control for spoken word and vocals
        /// Soft knee, moderate ratio, medium attack/release for natural sound
        /// </summary>
        VocalGentle,

        /// <summary>
        /// Aggressive vocal compression - strong control for consistent vocal levels
        /// Low threshold, high ratio, fast attack for broadcast-style vocal processing
        /// </summary>
        VocalAggressive,

        /// <summary>
        /// Drum compression - punchy transient control for percussive elements
        /// Medium threshold, moderate ratio, very fast attack to preserve punch
        /// </summary>
        Drums,

        /// <summary>
        /// Bass compression - tight low-end control for bass instruments
        /// Low threshold, high ratio, medium attack to maintain fundamental frequencies
        /// </summary>
        Bass,

        /// <summary>
        /// Mastering limiter - transparent limiting for final mix protection
        /// High threshold, very high ratio, ultra-fast attack for peak limiting
        /// </summary>
        MasteringLimiter,

        /// <summary>
        /// Vintage style - emulates classic analog compressor characteristics
        /// Medium settings with slower response for musical, warm compression
        /// </summary>
        Vintage
    }

    /// <summary>
    /// Professional compressor with consistent processing
    /// </summary>
    public sealed class CompressorEffect : IEffectProcessor
    {
        // IEffectProcessor implementation
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;

        // Audio configuration
        private AudioConfig? _config;

        // Parameters
        private float _threshold = 0.5f;
        private float _ratio = 4.0f;
        private float _attackTime = 0.1f;
        private float _releaseTime = 0.2f;
        private float _makeupGain = 1.0f;
        private float _sampleRate = 44100f;

        // Internal DSP State - Cached for performance
        private float _envelope = 0.0f;
        
        // Pre-calculated Coefficients
        private float _attackCoeff;
        private float _releaseCoeff;
        private float _thresholdDb;
        private float _slope; // 1.0/ratio - 1.0
        
        // Soft Knee cached values
        private const float KneeWidthDb = 6.0f;
        private const float KneeHalfWidth = KneeWidthDb / 2.0f;
        private float _kneeLowerBoundDb;
        private float _kneeUpperBoundDb;

        /// <summary>
        /// Gets the unique identifier for this effect.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Gets or sets the name of the effect.
        /// </summary>
        public string Name
        {
            get => _name;
            set => _name = value ?? "Compressor";
        }

        /// <summary>
        /// Gets or sets whether the effect is enabled.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Mix between dry and wet signal (0.0 - 1.0). Compressor doesn't use traditional mix, always returns 1.0.
        /// </summary>
        public float Mix
        {
            get => 1.0f;
            set { } // Compressor is always 100% processed
        }

        /// <summary>
        /// Constructor with all parameters
        /// </summary>
        /// <param name="threshold">Threshold level in range [0,1]</param>
        /// <param name="ratio">Compression ratio (N:1)</param>
        /// <param name="attackTime">Attack time in milliseconds</param>
        /// <param name="releaseTime">Release time in milliseconds</param>
        /// <param name="makeupGain">Makeup gain as linear amplitude multiplier</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        public CompressorEffect(float threshold = 0.5f, float ratio = 4.0f, float attackTime = 100f,
                         float releaseTime = 200f, float makeupGain = 1.0f, float sampleRate = 44100f)
        {
            _id = Guid.NewGuid();
            _name = "Compressor";
            _enabled = true;

            // Initialize directly without property overhead for constructor
            _threshold = FastClamp(threshold, 0.0f, 1.0f);
            _ratio = FastClamp(ratio, 1.0f, 100.0f);
            // Convert ms to seconds
            _attackTime = FastClamp(attackTime, 0.1f, 1000f) / 1000f; 
            _releaseTime = FastClamp(releaseTime, 1f, 2000f) / 1000f;
            _makeupGain = FastClamp(makeupGain, 0.1f, 10.0f);
            _sampleRate = FastClamp(sampleRate, 8000f, 192000f);

            RecalculateCoefficients();
        }

        /// <summary>
        /// Constructor with preset selection
        /// </summary>
        /// <param name="preset">Compressor preset to apply</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        public CompressorEffect(CompressorPreset preset, float sampleRate = 44100f)
        {
            _id = Guid.NewGuid();
            _name = "Compressor";
            _enabled = true;
            _sampleRate = FastClamp(sampleRate, 8000f, 192000f);
            
            SetPreset(preset); // This will trigger RecalculateCoefficients
        }

        /// <summary>
        /// Initializes the effect with the specified audio configuration.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            if (Math.Abs(_sampleRate - config.SampleRate) > 0.1f)
            {
                _sampleRate = config.SampleRate;
                RecalculateCoefficients();
            }
        }

        /// <summary>
        /// Update all internal coefficients based on current parameters.
        /// call this whenever a parameter changes.
        /// </summary>
        private void RecalculateCoefficients()
        {
            // Attack/Release coefficients
            // Formula: coeff = exp(-1 / (time * sampleRate))
            // We use MathF for performance in float context
            _attackCoeff = MathF.Exp(-1.0f / (_sampleRate * _attackTime));
            _releaseCoeff = MathF.Exp(-1.0f / (_sampleRate * _releaseTime));

            // Threshold in dB
            // Protect against log(0)
            _thresholdDb = 20.0f * MathF.Log10(Math.Max(_threshold, 1e-6f));

            // Slope for compression
            // Output = Threshold + (Input - Threshold) / Ratio
            // GainReduction = (Input - Threshold) * (1/Ratio - 1)
            _slope = 1.0f / _ratio - 1.0f;

            // Soft Knee bounds
            _kneeLowerBoundDb = _thresholdDb - KneeHalfWidth;
            _kneeUpperBoundDb = _thresholdDb + KneeHalfWidth;
        }

        /// <summary>
        /// Compressor process with consistent operation
        /// </summary>
        /// <param name="buffer">Audio samples to process in-place</param>
        /// <param name="frameCount">The number of frames in the buffer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled)
                return;

            // Local copies of coefficients for register reuse/avoiding `this` indirection
            float env = _envelope;
            float att = _attackCoeff;
            float rel = _releaseCoeff;
            float mkp = _makeupGain;
            float slope = _slope;
            float tDb = _thresholdDb;
            float kLower = _kneeLowerBoundDb;
            float kUpper = _kneeUpperBoundDb;
            
            // Calculate total samples
            int totalSamples = frameCount * _config.Channels;

            for (int i = 0; i < totalSamples; i++)
            {
                float input = buffer[i];
                float absInput = Math.Abs(input);

                // 1. Envelope Detection (Peak Detector with release)
                // Branchless approach could be used but standard branching is predictable here
                if (absInput > env)
                {
                    env = att * env + (1.0f - att) * absInput;
                }
                else
                {
                    env = rel * env + (1.0f - rel) * absInput;
                }

                // 2. Gain Calculation
                // Avoid log of zero
                if (env < 1e-6f) 
                {
                    // Signal is too small, no compression needed usually, but we apply makeup
                    // To stay consistent with silence
                    buffer[i] = input * mkp;
                    continue; 
                }

                // Convert envelope to dB
                // Approximation is suitable for dynamics processing and much faster
                // We'll use standard Log10 here for accuracy as requested for "Professional"
                // but MathF version is faster than double Math.Log10
                float envDb = 20.0f * MathF.Log10(env);

                float gainReductionDb = 0.0f;

                // 3. Compression Characteristic (Soft Knee)
                if (envDb < kLower)
                {
                    // Below knee - linear region (1:1), no reduction
                    gainReductionDb = 0.0f;
                }
                else if (envDb > kUpper)
                {
                    // Above knee - constant ratio compression
                    gainReductionDb = slope * (envDb - tDb);
                }
                else
                {
                    // Inside soft knee - quadratic interpolation
                    // Formula: slope * ((x - (T - W/2))^2) / (2 * W)
                    float over = envDb - kLower; // x - lower_bound
                    gainReductionDb = slope * (over * over) / (2.0f * KneeWidthDb);
                }

                // 4. Application
                // DB to Linear
                // gain = 10^(dB/20)
                float currentGain = MathF.Pow(10.0f, gainReductionDb * 0.05f);

                // Apply makeup gain and compression
                float combinedGain = currentGain * mkp;

                // 5. Output Limiting / Safety
                // Hard limit at 0dB (1.0) to prevent clipping if makeup is too high
                // This is a safety feature for the "internal" professional sound
                float output = input * combinedGain;
                if (output > 1.0f) output = 1.0f;
                else if (output < -1.0f) output = -1.0f;

                buffer[i] = output;
            }

            // Save state
            _envelope = env;
            // Prevent denormal numbers which can slow down CPU
            if (_envelope < 1e-10f) _envelope = 0.0f;
        }

        /// <summary>
        /// Set compressor parameters using predefined presets
        /// </summary>
        /// <param name="preset">The preset to apply</param>
        public void SetPreset(CompressorPreset preset)
        {
            switch (preset)
            {
                case CompressorPreset.Default:
                    _threshold = 0.5f;     // -6 dB 
                    _ratio = 4.0f; 
                    _attackTime = 0.1f;    // 100ms
                    _releaseTime = 0.2f;   // 200ms
                    _makeupGain = 1.0f;
                    break;

                case CompressorPreset.VocalGentle:
                    _threshold = 0.7f;     // ~ -3 dB
                    _ratio = 3.0f;
                    _attackTime = 0.015f;  // 15ms
                    _releaseTime = 0.150f; // 150ms
                    _makeupGain = 1.2f;
                    break;

                case CompressorPreset.VocalAggressive:
                    _threshold = 0.35f;    // ~ -9 dB
                    _ratio = 8.0f;
                    _attackTime = 0.005f;  // 5ms
                    _releaseTime = 0.100f; // 100ms
                    _makeupGain = 2.5f;
                    break;

                case CompressorPreset.Drums:
                    _threshold = 0.6f;
                    _ratio = 4.5f;
                    _attackTime = 0.001f;  // 1ms
                    _releaseTime = 0.080f; // 80ms
                    _makeupGain = 1.8f;
                    break;

                case CompressorPreset.Bass:
                    _threshold = 0.45f;
                    _ratio = 6.0f;
                    _attackTime = 0.010f;  // 10ms
                    _releaseTime = 0.200f; // 200ms
                    _makeupGain = 2.0f;
                    break;

                case CompressorPreset.MasteringLimiter:
                    _threshold = 0.9f;
                    _ratio = 20.0f;
                    _attackTime = 0.0001f; // 0.1ms
                    _releaseTime = 0.050f; // 50ms
                    _makeupGain = 1.0f;
                    break;

                case CompressorPreset.Vintage:
                    _threshold = 0.55f;
                    _ratio = 3.5f;
                    _attackTime = 0.025f;  // 25ms
                    _releaseTime = 0.300f; // 300ms
                    _makeupGain = 1.6f;
                    break;
            }
            RecalculateCoefficients();
        }

        /// <summary>
        /// Resets internal state but preserves current parameter settings
        /// </summary>
        public void Reset()
        {
            _envelope = 0.0f;
        }

        /// <summary>
        /// Disposes the compressor effect and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            Reset();
            _disposed = true;
        }

        /// <summary>
        /// Threshold level in range [0,1]
        /// </summary>
        public float Threshold
        {
            get => _threshold;
            set
            {
                if (Math.Abs(_threshold - value) > 0.001f)
                {
                    _threshold = FastClamp(value, 0.0f, 1.0f);
                    RecalculateCoefficients();
                }
            }
        }

        /// <summary>
        /// Compression ratio (N:1)
        /// </summary>
        public float Ratio
        {
            get => _ratio;
            set
            {
                if (Math.Abs(_ratio - value) > 0.01f)
                {
                    _ratio = FastClamp(value, 1.0f, 100.0f);
                    RecalculateCoefficients();
                }
            }
        }

        /// <summary>
        /// Attack time in milliseconds
        /// </summary>
        public float AttackTime
        {
            get => _attackTime * 1000f;
            set
            {
                float newTime = FastClamp(value, 0.1f, 1000f) / 1000f;
                if (Math.Abs(_attackTime - newTime) > 0.00001f)
                {
                    _attackTime = newTime;
                    RecalculateCoefficients();
                }
            }
        }

        /// <summary>
        /// Release time in milliseconds
        /// </summary>
        public float ReleaseTime
        {
            get => _releaseTime * 1000f;
            set
            {
                float newTime = FastClamp(value, 1f, 2000f) / 1000f;
                if (Math.Abs(_releaseTime - newTime) > 0.00001f)
                {
                    _releaseTime = newTime;
                    RecalculateCoefficients();
                }
            }
        }

        /// <summary>
        /// Makeup gain as linear amplitude multiplier
        /// </summary>
        public float MakeupGain
        {
            get => _makeupGain;
            set => _makeupGain = FastClamp(value, 0.1f, 10.0f);
        }

        /// <summary>
        /// Sample rate in Hz
        /// </summary>
        public float SampleRate
        {
            get => _sampleRate;
            set
            {
                if (Math.Abs(_sampleRate - value) > 1.0f)
                {
                    _sampleRate = FastClamp(value, 8000f, 192000f);
                    RecalculateCoefficients();
                }
            }
        }

        /// <summary>
        /// Converts linear amplitude to decibels
        /// </summary>
        public static float LinearToDb(float linear)
        {
            return 20f * MathF.Log10(Math.Max(linear, 1e-6f));
        }

        /// <summary>
        /// Converts decibels to linear amplitude
        /// </summary>
        public static float DbToLinear(float dB)
        {
            return MathF.Pow(10f, dB / 20f);
        }

        public override string ToString()
        {
            return $"Compressor: Threshold={_threshold:F2}, Ratio={_ratio:F1}:1, Attack={AttackTime:F1}ms, Release={ReleaseTime:F1}ms, Enabled={_enabled}";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastClamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
