using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Limiter presets for different audio processing scenarios
    /// </summary>
    public enum LimiterPreset
    {
        /// <summary>
        /// Default preset with balanced settings
        /// </summary>
        Default,

        /// <summary>
        /// Mastering limiter - transparent peak control for final mix
        /// Conservative threshold, brick wall ceiling, medium release for transparent limiting
        /// </summary>
        Mastering,

        /// <summary>
        /// Broadcast limiter - aggressive peak control for radio/streaming
        /// Lower threshold, tight ceiling, fast release for consistent loudness
        /// </summary>
        Broadcast,

        /// <summary>
        /// Live performance - reliable peak protection for live audio
        /// Medium threshold, safe ceiling, fast response for real-time protection
        /// </summary>
        Live,

        /// <summary>
        /// Drum bus limiter - punchy transient limiting for drum groups
        /// High threshold, quick response, short lookahead to maintain punch
        /// </summary>
        DrumBus,

        /// <summary>
        /// Vocal safety - gentle limiting for vocal recordings
        /// High threshold, soft ceiling, longer release for natural vocal sound
        /// </summary>
        VocalSafety,

        /// <summary>
        /// Bass limiting - specialized limiting for low-frequency content
        /// Medium threshold, controlled release to avoid pumping on bass content
        /// </summary>
        Bass,

        /// <summary>
        /// Podcast/dialog - optimized for spoken word content
        /// Conservative settings with smooth response for speech intelligibility
        /// </summary>
        Podcast,

        /// <summary>
        /// Aggressive style - heavy limiting for electronic/dance music
        /// Low threshold, tight ceiling, fast response for loud, consistent output
        /// </summary>
        Aggressive
    }

    /// <summary>
    /// Professional audio limiter with look-ahead and smooth gain reduction
    /// </summary>
    public sealed class LimiterEffect : IEffectProcessor
    {
        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig _config;

        private float[] _delayBuffer;
        private float[] _envelopeBuffer;
        private int _delayIndex;
        private int _envelopeIndex;
        private float _currentGain;
        private float _targetGain;
        private readonly float _sampleRate;

        // Limiter parameters
        private float _threshold;
        private float _ceiling;
        private float _release;
        private float _lookAheadMs;
        private int _lookAheadSamples;

        // Constants
        private const float DEFAULT_THRESHOLD = -3.0f;  // dB
        private const float DEFAULT_CEILING = -0.1f;    // dB
        private const float DEFAULT_RELEASE = 50.0f;    // ms
        private const float DEFAULT_LOOKAHEAD = 5.0f;   // ms

        // Parameter limits
        private const float MIN_THRESHOLD = -20.0f;     // dB
        private const float MAX_THRESHOLD = 0.0f;       // dB
        private const float MIN_CEILING = -2.0f;        // dB
        private const float MAX_CEILING = 0.0f;         // dB
        private const float MIN_RELEASE = 1.0f;         // ms
        private const float MAX_RELEASE = 1000.0f;      // ms
        private const float MIN_LOOKAHEAD = 1.0f;       // ms
        private const float MAX_LOOKAHEAD = 20.0f;      // ms

        /// <summary>
        /// Gets the unique identifier for this effect instance
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Gets the name of this effect
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Gets or sets whether this effect is enabled
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Mix property for interface compliance (always 1.0 for limiters)
        /// </summary>
        public float Mix
        {
            get => 1.0f;
            set { /* Limiters don't use mix - always 100% */ }
        }

        /// <summary>
        /// Professional limiter constructor with all parameters
        /// </summary>
        /// <param name="sampleRate">Sample rate</param>
        /// <param name="threshold">Threshold in dB (default: -3dB)</param>
        /// <param name="ceiling">Output ceiling in dB (default: -0.1dB)</param>
        /// <param name="release">Release time in ms (default: 50ms)</param>
        /// <param name="lookAheadMs">Look-ahead time in ms (default: 5ms)</param>
        public LimiterEffect(float sampleRate, float threshold = DEFAULT_THRESHOLD,
            float ceiling = DEFAULT_CEILING, float release = DEFAULT_RELEASE,
            float lookAheadMs = DEFAULT_LOOKAHEAD)
        {
            _id = Guid.NewGuid();
            _name = "Limiter";
            _enabled = true;

            _sampleRate = sampleRate;

            // Set parameters with validation
            Threshold = threshold;
            Ceiling = ceiling;
            Release = release;
            LookAheadMs = lookAheadMs;

            // Initialize buffers
            _delayBuffer = new float[_lookAheadSamples];
            _envelopeBuffer = new float[_lookAheadSamples];

            _currentGain = 1.0f;
            _targetGain = 1.0f;
            _delayIndex = 0;
            _envelopeIndex = 0;
        }

        /// <summary>
        /// Professional limiter constructor with preset selection
        /// </summary>
        /// <param name="sampleRate">Sample rate</param>
        /// <param name="preset">Preset to use</param>
        public LimiterEffect(float sampleRate, LimiterPreset preset)
        {
            _id = Guid.NewGuid();
            _name = "Limiter";
            _enabled = true;

            _sampleRate = sampleRate;

            // Initialize with default values first
            _threshold = DbToLinear(DEFAULT_THRESHOLD);
            _ceiling = DbToLinear(DEFAULT_CEILING);
            _release = CalculateReleaseCoeff(DEFAULT_RELEASE, sampleRate);
            _lookAheadMs = DEFAULT_LOOKAHEAD;
            _lookAheadSamples = (int)(DEFAULT_LOOKAHEAD * sampleRate / 1000.0f);

            // Initialize buffers
            _delayBuffer = new float[_lookAheadSamples];
            _envelopeBuffer = new float[_lookAheadSamples];

            _currentGain = 1.0f;
            _targetGain = 1.0f;
            _delayIndex = 0;
            _envelopeIndex = 0;

            // Apply preset
            SetPreset(preset);
        }

        /// <summary>
        /// Initialize the effect with audio configuration
        /// </summary>
        /// <param name="config">Audio configuration</param>
        public void Initialize(AudioConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Gets or sets the threshold in dB
        /// </summary>
        public float Threshold
        {
            get => LinearToDb(_threshold);
            set
            {
                float clampedValue = Math.Clamp(value, MIN_THRESHOLD, MAX_THRESHOLD);
                _threshold = DbToLinear(clampedValue);
            }
        }

        /// <summary>
        /// Gets or sets the ceiling in dB
        /// </summary>
        public float Ceiling
        {
            get => LinearToDb(_ceiling);
            set
            {
                float clampedValue = Math.Clamp(value, MIN_CEILING, MAX_CEILING);
                _ceiling = DbToLinear(clampedValue);
            }
        }

        /// <summary>
        /// Gets or sets the release time in ms
        /// </summary>
        public float Release
        {
            get => -1000.0f / MathF.Log(1.0f - _release) / _sampleRate;
            set
            {
                float clampedValue = Math.Clamp(value, MIN_RELEASE, MAX_RELEASE);
                _release = CalculateReleaseCoeff(clampedValue, _sampleRate);
            }
        }

        /// <summary>
        /// Gets or sets the look-ahead time in ms
        /// </summary>
        public float LookAheadMs
        {
            get => _lookAheadMs;
            set
            {
                float clampedValue = Math.Clamp(value, MIN_LOOKAHEAD, MAX_LOOKAHEAD);
                _lookAheadMs = clampedValue;
                int newLookAheadSamples = (int)(clampedValue * _sampleRate / 1000.0f);

                if (newLookAheadSamples != _lookAheadSamples)
                {
                    _lookAheadSamples = newLookAheadSamples;
                    // Reinitialize buffers with new size
                    Array.Resize(ref _delayBuffer, _lookAheadSamples);
                    Array.Resize(ref _envelopeBuffer, _lookAheadSamples);
                    Reset();
                }
            }
        }

        /// <summary>
        /// Process audio samples with professional limiting
        /// </summary>
        /// <param name="buffer">Audio buffer to process</param>
        /// <param name="frameCount">Number of frames to process</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled)
                return;

            int sampleCount = frameCount * _config.Channels;

            for (int i = 0; i < sampleCount; i++)
            {
                float inputSample = buffer[i];

                // Store input in delay buffer
                _delayBuffer[_delayIndex] = inputSample;

                // Calculate peak level for look-ahead detection
                float peakLevel = GetPeakLevel();

                // Calculate required gain reduction
                float requiredGain = CalculateGainReduction(peakLevel);

                // Store gain in envelope buffer for smooth transitions
                _envelopeBuffer[_envelopeIndex] = requiredGain;

                // Get smoothed gain from envelope buffer
                float smoothGain = GetSmoothedGain();

                // Apply gain reduction to delayed sample
                float delayedSample = _delayBuffer[(_delayIndex - _lookAheadSamples + _delayBuffer.Length) % _delayBuffer.Length];
                float processedSample = delayedSample * smoothGain;

                // Apply final ceiling limit
                processedSample = ApplyCeiling(processedSample);

                buffer[i] = processedSample;

                // Update buffer indices
                _delayIndex = (_delayIndex + 1) % _delayBuffer.Length;
                _envelopeIndex = (_envelopeIndex + 1) % _envelopeBuffer.Length;
            }
        }

        /// <summary>
        /// Reset limiter state
        /// </summary>
        public void Reset()
        {
            Array.Clear(_delayBuffer);
            Array.Clear(_envelopeBuffer);
            _currentGain = 1.0f;
            _targetGain = 1.0f;
            _delayIndex = 0;
            _envelopeIndex = 0;
        }

        /// <summary>
        /// Dispose of resources
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        /// <summary>
        /// Returns a string representation of this effect
        /// </summary>
        public override string ToString()
        {
            return $"{_name} (Enabled: {_enabled}, Threshold: {Threshold:F1}dB, Ceiling: {Ceiling:F1}dB)";
        }

        /// <summary>
        /// Set limiter parameters using predefined presets
        /// </summary>
        public void SetPreset(LimiterPreset preset)
        {
            switch (preset)
            {
                case LimiterPreset.Default:
                    // Default balanced settings
                    Threshold = DEFAULT_THRESHOLD;
                    Ceiling = DEFAULT_CEILING;
                    Release = DEFAULT_RELEASE;
                    LookAheadMs = DEFAULT_LOOKAHEAD;
                    break;

                case LimiterPreset.Mastering:
                    // Transparent mastering limiting - catches only the peaks
                    // High threshold for minimal processing, conservative ceiling
                    Threshold = -1.0f;    // -1 dB - only catches true peaks
                    Ceiling = -0.1f;      // -0.1 dB - safe digital ceiling
                    Release = 100f;       // 100ms - smooth, transparent release
                    LookAheadMs = 8.0f;   // Longer lookahead for transparency
                    break;

                case LimiterPreset.Broadcast:
                    // Aggressive broadcast limiting for consistent loudness
                    // Lower threshold for tighter control, fast release to avoid pumping
                    Threshold = -6.0f;    // -6 dB - catches more of the signal
                    Ceiling = -0.3f;      // -0.3 dB - extra safety margin for broadcast
                    Release = 25f;        // 25ms - fast release for consistency
                    LookAheadMs = 5.0f;   // Standard lookahead
                    break;

                case LimiterPreset.Live:
                    // Live performance protection - reliable peak limiting
                    // Balanced settings for real-time protection without artifacts
                    Threshold = -3.0f;    // -3 dB - good balance of control and transparency
                    Ceiling = -0.5f;      // -0.5 dB - extra safety for live systems
                    Release = 50f;        // 50ms - medium release for stability
                    LookAheadMs = 3.0f;   // Shorter lookahead for live use
                    break;

                case LimiterPreset.DrumBus:
                    // Drum bus limiting - preserves transient punch
                    // Higher threshold to preserve drum transients, quick response
                    Threshold = -2.0f;    // -2 dB - allows drum punch through
                    Ceiling = -0.1f;      // -0.1 dB - standard ceiling
                    Release = 15f;        // 15ms - fast release to avoid sustain limiting
                    LookAheadMs = 2.0f;   // Short lookahead to maintain punch
                    break;

                case LimiterPreset.VocalSafety:
                    // Vocal safety limiting - gentle peak control for vocals
                    // High threshold for natural sound, longer release for smoothness
                    Threshold = -4.0f;    // -4 dB - gentle vocal limiting
                    Ceiling = -0.2f;      // -0.2 dB - safe ceiling
                    Release = 200f;       // 200ms - smooth, natural release for vocals
                    LookAheadMs = 10.0f;  // Longer lookahead for smooth vocal processing
                    break;

                case LimiterPreset.Bass:
                    // Bass limiting - specialized for low-frequency content
                    // Prevents bass from causing system overload, controlled release
                    Threshold = -5.0f;    // -5 dB - controls bass dynamics
                    Ceiling = -0.1f;      // -0.1 dB - standard ceiling
                    Release = 150f;       // 150ms - prevents pumping on bass content
                    LookAheadMs = 6.0f;   // Medium lookahead for bass control
                    break;

                case LimiterPreset.Podcast:
                    // Podcast/dialog limiting - optimized for speech
                    // Conservative settings that maintain speech intelligibility
                    Threshold = -8.0f;    // -8 dB - gentle limiting for speech
                    Ceiling = -0.5f;      // -0.5 dB - extra safety for dialog
                    Release = 300f;       // 300ms - slow, smooth release for natural speech
                    LookAheadMs = 12.0f;  // Longer lookahead for smooth speech processing
                    break;

                case LimiterPreset.Aggressive:
                    // Aggressive limiting for electronic/dance music
                    // Heavy limiting for maximum loudness and consistency
                    Threshold = -10.0f;   // -10 dB - heavy limiting
                    Ceiling = -0.1f;      // -0.1 dB - standard ceiling
                    Release = 10f;        // 10ms - very fast release for aggressive sound
                    LookAheadMs = 3.0f;   // Short lookahead for aggressive response
                    break;
            }
        }

        /// <summary>
        /// Convert linear amplitude to decibels
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float LinearToDb(float linear)
        {
            return 20.0f * MathF.Log10(Math.Max(linear, 1e-6f));
        }

        /// <summary>
        /// Get peak level from look-ahead buffer
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetPeakLevel()
        {
            float peak = 0.0f;
            for (int i = 0; i < _delayBuffer.Length; i++)
            {
                float abs = Math.Abs(_delayBuffer[i]);
                if (abs > peak)
                    peak = abs;
            }
            return peak;
        }

        /// <summary>
        /// Calculate required gain reduction based on peak level
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float CalculateGainReduction(float peakLevel)
        {
            if (peakLevel <= _threshold)
                return 1.0f;

            // Calculate ratio of excess over threshold
            float excess = peakLevel / _threshold;
            float targetLevel = _threshold / excess;

            return Math.Max(targetLevel / peakLevel, 0.1f); // Minimum 10% gain
        }

        /// <summary>
        /// Get smoothed gain from envelope buffer
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetSmoothedGain()
        {
            // Find minimum gain in envelope buffer (most restrictive)
            float minGain = 1.0f;
            for (int i = 0; i < _envelopeBuffer.Length; i++)
            {
                if (_envelopeBuffer[i] < minGain)
                    minGain = _envelopeBuffer[i];
            }

            // Smooth gain changes
            _targetGain = minGain;

            if (_targetGain < _currentGain)
            {
                // Fast attack for gain reduction
                _currentGain = _targetGain;
            }
            else
            {
                // Slow release for gain recovery
                _currentGain += (_targetGain - _currentGain) * _release;
            }

            return _currentGain;
        }

        /// <summary>
        /// Apply final ceiling limit
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ApplyCeiling(float sample)
        {
            float abs = Math.Abs(sample);
            if (abs > _ceiling)
            {
                return sample > 0 ? _ceiling : -_ceiling;
            }
            return sample;
        }

        /// <summary>
        /// Convert dB to linear scale
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float DbToLinear(float db)
        {
            return MathF.Pow(10.0f, db / 20.0f);
        }

        /// <summary>
        /// Calculate release coefficient from time in ms
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float CalculateReleaseCoeff(float timeMs, float sampleRate)
        {
            return 1.0f - MathF.Exp(-1.0f / (timeMs * sampleRate / 1000.0f));
        }

        /// <summary>
        /// Get current gain reduction in dB for metering
        /// </summary>
        public float GetGainReductionDb()
        {
            return 20.0f * MathF.Log10(_currentGain);
        }

        /// <summary>
        /// Check if limiter is currently reducing gain
        /// </summary>
        public bool IsLimiting => _currentGain < 0.99f;
    }
}
