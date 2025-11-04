using Ownaudio.Processors;
using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Compression presets for different audio processing scenarios
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
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
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class Compressor : SampleProcessorBase
    {
        private float threshold = 0.5f;
        private float ratio = 4.0f;
        private float attackTime = 0.1f;
        private float releaseTime = 0.2f;
        private float makeupGain = 1.0f;
        private float envelope = 0.0f;
        private float sampleRate = 44100f;

        /// <summary>
        /// Constructor with all parameters
        /// </summary>
        /// <param name="threshold">Threshold level in range [0,1]</param>
        /// <param name="ratio">Compression ratio (N:1)</param>
        /// <param name="attackTime">Attack time in milliseconds</param>
        /// <param name="releaseTime">Release time in milliseconds</param>
        /// <param name="makeupGain">Makeup gain as linear amplitude multiplier</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        public Compressor(float threshold = 0.5f, float ratio = 4.0f, float attackTime = 100f,
                         float releaseTime = 200f, float makeupGain = 1.0f, float sampleRate = 44100f)
        {
            Threshold = threshold;
            Ratio = ratio;
            AttackTime = attackTime;
            ReleaseTime = releaseTime;
            MakeupGain = makeupGain;
            SampleRate = sampleRate;
        }

        /// <summary>
        /// Constructor with preset selection
        /// </summary>
        /// <param name="preset">Compressor preset to apply</param>
        public Compressor(CompressorPreset preset)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Compressor process with consistent operation
        /// </summary>
        /// <param name="samples">Audio samples to process in-place</param>
        public override void Process(Span<float> samples)
        {
            float attackCoeff = (float)Math.Exp(-1.0 / (sampleRate * attackTime));
            float releaseCoeff = (float)Math.Exp(-1.0 / (sampleRate * releaseTime));

            for (int i = 0; i < samples.Length; i++)
            {
                float inputLevel = Math.Abs(samples[i]);

                // Envelope follower
                if (inputLevel > envelope)
                    envelope = attackCoeff * envelope + (1 - attackCoeff) * inputLevel;
                else
                    envelope = releaseCoeff * envelope + (1 - releaseCoeff) * inputLevel;

                float gainReduction = ApplyCompression();
                samples[i] *= gainReduction * makeupGain;
            }
        }

        /// <summary>
        /// Applies compression with output range protection
        /// </summary>
        /// <returns>Gain reduction factor with safe output limiting</returns>
        private float ApplyCompression()
        {
            float safeEnvelope = Math.Max(envelope, 1e-6f);
            float safeThreshold = Math.Max(threshold, 1e-6f);

            if (safeEnvelope > safeThreshold)
            {
                float levelDb = 20 * (float)Math.Log10(safeEnvelope);
                float thresholdDb = 20 * (float)Math.Log10(safeThreshold);

                float compressedDb = thresholdDb + (levelDb - thresholdDb) / ratio;
                float gainReductionDb = Math.Max(compressedDb - levelDb, -60f);

                float baseGainReduction = (float)Math.Pow(10, gainReductionDb / 20);

                float maximumSafeGain = 0.95f / Math.Max(safeEnvelope, 1e-6f);
                float totalGain = baseGainReduction * makeupGain;

                // Limit total gain to prevent clipping
                if (totalGain > maximumSafeGain)
                {
                    return maximumSafeGain;
                }

                return baseGainReduction;
            }

            // When below threshold, still check if makeup gain alone would cause clipping
            float noCompressionGain = 1.0f * makeupGain;
            float maxSafeGain = 0.95f / Math.Max(safeEnvelope, 1e-6f);

            if (noCompressionGain > maxSafeGain)
            {
                return maxSafeGain;
            }

            return 1.0f;
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
                    // Default balanced settings for general use
                    Threshold = 0.5f;     // -6 dB - moderate threshold
                    Ratio = 4.0f;         // 4:1 - standard compression ratio
                    AttackTime = 100f;    // 100ms - balanced attack time
                    ReleaseTime = 200f;   // 200ms - balanced release time
                    MakeupGain = 1.0f;    // 0 dB - no makeup gain by default
                    break;

                case CompressorPreset.VocalGentle:
                    // Gentle vocal processing for natural sound
                    // Higher threshold to catch only louder parts, moderate ratio for musicality
                    Threshold = 0.7f;     // -2.9 dB - catches peaks without over-processing
                    Ratio = 3.0f;         // 3:1 - musical compression ratio
                    AttackTime = 15f;     // 15ms - fast enough for vocals, slow enough to preserve character
                    ReleaseTime = 150f;   // 150ms - natural release for smooth sound
                    MakeupGain = 1.2f;    // +1.6 dB - compensate for gentle reduction
                    break;

                case CompressorPreset.VocalAggressive:
                    // Strong vocal control for broadcast/podcast style
                    // Low threshold catches more signal, high ratio for consistent levels
                    Threshold = 0.35f;    // -9.1 dB - catches most of the vocal range
                    Ratio = 8.0f;         // 8:1 - strong compression for consistency
                    AttackTime = 5f;      // 5ms - fast attack for immediate control
                    ReleaseTime = 100f;   // 100ms - quick release to avoid pumping
                    MakeupGain = 2.5f;    // +8 dB - significant makeup gain needed
                    break;

                case CompressorPreset.Drums:
                    // Punchy drum compression preserving transients
                    // Balanced threshold, moderate ratio, very fast attack for transient control
                    Threshold = 0.6f;     // -4.4 dB - allows transient punch through
                    Ratio = 4.5f;         // 4.5:1 - controlled but punchy
                    AttackTime = 1f;      // 1ms - ultra-fast to catch transients
                    ReleaseTime = 80f;    // 80ms - quick release to avoid sustain compression
                    MakeupGain = 1.8f;    // +5.1 dB - restore punch
                    break;

                case CompressorPreset.Bass:
                    // Tight bass control maintaining fundamental frequencies
                    // Lower threshold for consistent low-end, high ratio for control
                    Threshold = 0.45f;    // -7 dB - controls bass dynamics effectively
                    Ratio = 6.0f;         // 6:1 - strong control for consistent low-end
                    AttackTime = 10f;     // 10ms - fast enough for control, slow enough for fundamentals
                    ReleaseTime = 200f;   // 200ms - slower release for smooth bass response
                    MakeupGain = 2.0f;    // +6 dB - restore bass presence
                    break;

                case CompressorPreset.MasteringLimiter:
                    // Transparent peak limiting for mastering
                    // High threshold for peaks only, extreme ratio for limiting
                    Threshold = 0.9f;     // -0.9 dB - only catches peaks
                    Ratio = 20.0f;        // 20:1 - limiting ratio
                    AttackTime = 0.1f;    // 0.1ms - instant attack for peak catching
                    ReleaseTime = 50f;    // 50ms - fast release to avoid pumping
                    MakeupGain = 1.0f;    // 0 dB - transparent processing
                    break;

                case CompressorPreset.Vintage:
                    // Classic analog compressor emulation
                    // Medium settings with slower response for vintage character
                    Threshold = 0.55f;    // -5.2 dB - moderate threshold
                    Ratio = 3.5f;         // 3.5:1 - vintage-style ratio
                    AttackTime = 25f;     // 25ms - slower attack for vintage character
                    ReleaseTime = 300f;   // 300ms - slow release for vintage smoothness
                    MakeupGain = 1.6f;    // +4 dB - vintage-style makeup gain
                    break;
            }
        }

        /// <summary>
        /// Resets internal state but preserves current parameter settings
        /// </summary>
        public override void Reset()
        {
            envelope = 0.0f;
        }

        /// <summary>
        /// Threshold level in range [0,1] where 1.0 = 0dB, 0.5 = -6dB
        /// Minimum: 0.0 (negative infinity dB)
        /// Maximum: 1.0 (0 dB)
        /// Default: 0.5 (-6 dB)
        /// </summary>
        public float Threshold
        {
            get => threshold;
            set => threshold = FastClamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Compression ratio (N:1)
        /// Minimum: 1.0 (no compression)
        /// Maximum: 100.0 (limiting)
        /// Default: 4.0 (4:1 compression)
        /// </summary>
        public float Ratio
        {
            get => ratio;
            set => ratio = FastClamp(value, 1.0f, 100.0f);
        }

        /// <summary>
        /// Attack time in milliseconds
        /// Minimum: 0.1 ms
        /// Maximum: 1000 ms
        /// Default: 100 ms
        /// </summary>
        public float AttackTime
        {
            get => attackTime * 1000f;
            set => attackTime = FastClamp(value, 0.1f, 1000f) / 1000f;
        }

        /// <summary>
        /// Release time in milliseconds
        /// Minimum: 1 ms
        /// Maximum: 2000 ms
        /// Default: 200 ms
        /// </summary>
        public float ReleaseTime
        {
            get => releaseTime * 1000f;
            set => releaseTime = FastClamp(value, 1f, 2000f) / 1000f;
        }

        /// <summary>
        /// Makeup gain as linear amplitude multiplier
        /// Minimum: 0.1 (approximately -20 dB)
        /// Maximum: 10.0 (+20 dB)
        /// Default: 1.0 (0 dB)
        /// </summary>
        public float MakeupGain
        {
            get => makeupGain;
            set => makeupGain = FastClamp(value, 0.1f, 10.0f);
        }

        /// <summary>
        /// Sample rate in Hz
        /// Minimum: 8000 Hz
        /// Maximum: 192000 Hz
        /// Default: 44100 Hz
        /// </summary>
        public float SampleRate
        {
            get => sampleRate;
            set => sampleRate = FastClamp(value, 8000f, 192000f);
        }

        /// <summary>
        /// Converts linear amplitude to decibels
        /// </summary>
        /// <param name="linear">Linear amplitude value</param>
        /// <returns>Value in decibels</returns>
        public static float LinearToDb(float linear)
        {
            return 20f * (float)Math.Log10(Math.Max(linear, 1e-6f));
        }

        /// <summary>
        /// Converts decibels to linear amplitude
        /// </summary>
        /// <param name="dB">Value in decibels</param>
        /// <returns>Linear amplitude value</returns>
        public static float DbToLinear(float dB)
        {
            return (float)Math.Pow(10f, dB / 20f);
        }

        /// <summary>
        /// Fast clamping function that constrains values to a specified range.
        /// </summary>
        /// <param name="value">The value to clamp.</param>
        /// <param name="min">Minimum allowed value.</param>
        /// <param name="max">Maximum allowed value.</param>
        /// <returns>The clamped value within the specified range.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastClamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
