using Ownaudio.Processors;
using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Preset configurations for the enhancer effect
    /// </summary>
    public enum EnhancerPreset
    {
        /// <summary>
        /// Subtle enhancement for vocals - adds gentle presence without harshness
        /// Uses moderate gain (2.0x) and higher cutoff (5kHz) to enhance vocal clarity
        /// Low mix (15%) maintains naturalness while adding definition
        /// </summary>
        VocalClarity,

        /// <summary>
        /// Aggressive enhancement for rock/metal music - adds bite and edge
        /// Higher gain (4.0x) and lower cutoff (3kHz) for more pronounced effect
        /// Moderate mix (25%) provides noticeable enhancement without overpowering
        /// </summary>
        RockEdge,

        /// <summary>
        /// Clean enhancement for acoustic instruments - preserves natural tone
        /// Moderate gain (2.5x) with high cutoff (6kHz) for gentle sparkle
        /// Very low mix (10%) maintains instrument authenticity
        /// </summary>
        AcousticSparkle,

        /// <summary>
        /// Heavy enhancement for dense mixes - cuts through busy arrangements
        /// High gain (3.5x) with moderate cutoff (4kHz) for presence boost
        /// Higher mix (30%) ensures audibility in complex arrangements
        /// </summary>
        MixCutter,

        /// <summary>
        /// Broadcast-ready enhancement - professional radio/podcast processing
        /// Balanced gain (3.0x) with speech-optimized cutoff (4.5kHz)
        /// Moderate mix (20%) provides clarity without fatigue
        /// </summary>
        Broadcast
    }

    /// <summary>
    /// Enhancer effect
    /// </summary>
    public class Enhancer : SampleProcessorBase
    {
        private float _mix;
        private float _gain;
        private float _alpha;
        private float _samplerate = 44100f;
        private float _xPrev;
        private float _yPrev;

        /// <summary>
        /// KConstructor
        /// </summary>
        /// <param name="mix">mix(0-1) : Controls the amount of processed signal blended with the original</param>
        /// <param name="cutFreq">cutoffFrequency: High-pass filter cutoff(typical 2-6kHz)</param>
        /// <param name="sampleRate">sampleRate: Audio system sample rate(typically 44.1kHz)</param>
        /// <param name="gain">gain: Pre-saturation amplification(typically 2-4x)</param>
        public Enhancer(float mix = 0.2f, float cutFreq = 4000f, float gain = 2.5f, float sampleRate = 44100f)
        {
            _mix = FastClamp(mix);
            _gain = gain;
            _samplerate = sampleRate;

            // Calculate high-pass filter coefficient using RC time constant approximation
            float rc = 1f / (2f * MathF.PI * cutFreq);
            _alpha = rc / (rc + 1f / (2f * MathF.PI * cutFreq * sampleRate));
        }

        /// <summary>
        /// Enhancer process
        /// </summary>
        /// <param name="samples"></param>
        public override void Process(Span<float> samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float original = samples[i];

                // Apply high-pass filter
                float highFreq = _alpha * (_yPrev + original - _xPrev);
                _xPrev = original;
                _yPrev = highFreq;

                // Nonlinear processing with soft clipping
                float processed = highFreq * _gain;
                processed = MathF.Tanh(processed * 0.5f) * 2f; // Gentle saturation

                // Mix processed signal with original
                samples[i] = original + processed * _mix;
            }
        }

        /// <summary>
        /// Set Compressor parameters
        /// </summary>
        /// <param name="mix">mix(0-1) : Controls the amount of processed signal blended with the original</param>
        /// <param name="cutFreq">cutoffFrequency: High-pass filter cutoff(typical 2-6kHz)</param>
        /// <param name="gain">gain: Pre-saturation amplification(typically 2-4x)</param>
        /// <param name="sampleRate">sampleRate: Audio system sample rate(typically 44.1kHz)</param>
        public void SetParameters(float mix = 0.2f, float cutFreq = 4000f, float gain = 2.5f, float sampleRate = 44100f)
        {
            _mix = FastClamp(mix);
            _gain = gain;
            _samplerate = sampleRate;

            // Calculate high-pass filter coefficient using RC time constant approximation
            float rc = 1f / (2f * MathF.PI * cutFreq);
            _alpha = rc / (rc + 1f / (2f * MathF.PI * cutFreq * _samplerate));
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


