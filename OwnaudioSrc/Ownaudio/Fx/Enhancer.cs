using System;
using Ownaudio.Processors;

namespace Ownaudio.Fx
{
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
            _mix = Math.Clamp(mix, 0f, 1f);
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
        public void SetParameters(float mix = 0.2f, float cutFreq = 4000f, float gain = 2.5f)
        {
            _mix = Math.Clamp(mix, 0f, 1f);
            _gain = gain;

            // Calculate high-pass filter coefficient using RC time constant approximation
            float rc = 1f / (2f * MathF.PI * cutFreq);
            _alpha = rc / (rc + 1f / (2f * MathF.PI * cutFreq * _samplerate));
        }
    }
}


