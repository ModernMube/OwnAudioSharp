using Ownaudio.Processors;
using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Compression presets for different audio processing scenarios
    /// </summary>
    public enum CompressorPreset
    {
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
    /// Compressor effekt
    /// </summary>
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
        /// Constructor with customizable parameters
        /// </summary>
        /// <param name="threshold">Threshold level in range [0,1]</param>
        /// <param name="ratio">Compression ratio</param>
        /// <param name="attackTime">Attack time in seconds</param>
        /// <param name="releaseTime">Release time in seconds</param>
        /// <param name="makeupGain">Envelope follower state</param>
        /// <param name="sampleRate">Sample rate</param>
        public Compressor(float threshold = 0.5f, float ratio = 4.0f, float attackTime = 100f, float releaseTime = 200f, float makeupGain = 1.0f, float sampleRate = 44100f)
        {
            this.threshold = threshold;
            this.ratio = ratio;
            this.attackTime = attackTime / 1000f;
            this.releaseTime = releaseTime / 1000f;
            this.makeupGain = makeupGain;
            this.sampleRate = sampleRate;
        }

        /// <summary>
        /// Compressor process
        /// </summary>
        /// <param name="samples"></param>
        public override void Process(Span<float> samples)
        {
            // Constants for envelope follower
            float attackCoeff = (float)Math.Exp(-1.0 / (sampleRate * attackTime));
            float releaseCoeff = (float)Math.Exp(-1.0 / (sampleRate * releaseTime));

            for (int i = 0; i < samples.Length; i++)
            {
                float inputLevel = Math.Abs(samples[i]);  // Get absolute value of sample for level detection


                if (inputLevel > envelope)   // Envelope follower
                    envelope = attackCoeff * envelope + (1 - attackCoeff) * inputLevel;
                else
                    envelope = releaseCoeff * envelope + (1 - releaseCoeff) * inputLevel;


                float gainReduction = 1.0f;   // Calculate gain reduction
                if (envelope > threshold)
                {
                    // Convert to dB for easier calculation
                    float levelDb = 20 * (float)Math.Log10(envelope);
                    float thresholdDb = 20 * (float)Math.Log10(threshold);

                    float compressedDb = thresholdDb + (levelDb - thresholdDb) / ratio; // Apply compression ratio

                    gainReduction = (float)Math.Pow(10, (compressedDb - levelDb) / 20); // Convert back to linear gain
                }

                samples[i] *= gainReduction * makeupGain; // Apply compression and makeup gain
            }
        }

        /// <summary>
        /// Set compressor parameters using predefined presets
        /// </summary>
        public void SetPreset(CompressorPreset preset)
        {
            switch (preset)
            {
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
        /// Resets the compressor's internal state by clearing the envelope follower.
        /// Does not modify any settings or parameters.
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
            set => threshold = FastClamp(value);
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
            set => ratio = Math.Max(1f, value);
        }

        /// <summary>
        /// Attack time in seconds
        /// Minimum: 1 ms
        /// Maximum: 1000 ms
        /// Default: 100 ms
        /// </summary>
        public float AttackTime
        {
            get => attackTime * 1000f;
            set => attackTime = Math.Max(1f, value) / 1000f;
        }

        /// <summary>
        /// Release time in seconds
        /// Minimum: 1 ms
        /// Maximum: 2000 ms
        /// Default: 200 ms
        /// </summary>
        public float ReleaseTime
        {
            get => releaseTime * 1000f;
            set => releaseTime = Math.Max(1f, value) / 1000f;
        }

        /// <summary>
        /// Makeup gain as linear amplitude multiplier
        /// Minimum: 0.0 (-infinity dB)
        /// Maximum: 10.0 (+20 dB)
        /// Default: 1.0 (0 dB)
        /// </summary>
        public float MakeupGain
        {
            get => makeupGain;
            set => makeupGain = Math.Max(0f, value);
        }

        /// <summary>
        /// Sample rate
        /// </summary>
        public float SampleRate
        {
            get => sampleRate;
            set => sampleRate = Math.Max(1f, value);
        }

        /// <summary>
        /// Converts linear amplitude to decibels
        /// </summary>
        public static float LinearToDb(float linear)
        {
            return 20f * (float)Math.Log10(Math.Max(linear, 1e-6f));
        }

        /// <summary>
        /// Converts decibels to linear amplitude
        /// </summary>
        public static float DbToLinear(float dB)
        {
            return (float)Math.Pow(10f, dB / 20f);
        }

        /// <summary>
        /// Fast audio clamping function that constrains values to the valid audio range [-1.0, 1.0].
        /// </summary>
        /// <param name="value">The audio sample value to clamp.</param>
        /// <returns>The clamped value within the range [-1.0, 1.0].</returns>
        /// <remarks>
        /// This method is aggressively inlined for maximum performance in audio processing loops.
        /// Audio clamping is essential to prevent:
        /// - Digital audio clipping and distortion
        /// - Hardware damage from excessive signal levels
        /// - Unwanted artifacts in the audio output
        /// 
        /// Values below -1.0 are clamped to -1.0, values above 1.0 are clamped to 1.0,
        /// and values within the valid range are passed through unchanged.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastClamp(float value)
        {
            return value < 0.0f ? 0.0f : (value > 1.0f ? 1.0f : value);
        }
    }
}
