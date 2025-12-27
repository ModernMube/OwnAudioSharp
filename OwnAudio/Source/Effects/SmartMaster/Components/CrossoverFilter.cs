using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Crossover filter - frequency split to L/R and Sub branches
    /// Linkwitz-Riley 4th order (2x Butterworth cascaded)
    /// </summary>
    public class CrossoverFilter
    {
        /// <summary>
        /// Sample rate in Hz.
        /// </summary>
        private float _sampleRate;
        
        /// <summary>
        /// Crossover frequency in Hz.
        /// </summary>
        private float _crossoverFreq;
        
        /// <summary>
        /// Lowpass filter coefficients - first Butterworth stage (b0).
        /// </summary>
        private float _lpB0_1, _lpB1_1, _lpB2_1, _lpA1_1, _lpA2_1;
        
        /// <summary>
        /// Lowpass filter coefficients - second Butterworth stage (b0).
        /// </summary>
        private float _lpB0_2, _lpB1_2, _lpB2_2, _lpA1_2, _lpA2_2;
        
        /// <summary>
        /// Highpass filter coefficients - first Butterworth stage (b0).
        /// </summary>
        private float _hpB0_1, _hpB1_1, _hpB2_1, _hpA1_1, _hpA2_1;
        
        /// <summary>
        /// Highpass filter coefficients - second Butterworth stage (b0).
        /// </summary>
        private float _hpB0_2, _hpB1_2, _hpB2_2, _hpA1_2, _hpA2_2;
        
        /// <summary>
        /// Lowpass filter state variables for stage 1, z1 (per channel).
        /// </summary>
        private float[] _lpZ1_1 = new float[2];
        
        /// <summary>
        /// Lowpass filter state variables for stage 1, z2 (per channel).
        /// </summary>
        private float[] _lpZ2_1 = new float[2];
        
        /// <summary>
        /// Lowpass filter state variables for stage 2, z1 (per channel).
        /// </summary>
        private float[] _lpZ1_2 = new float[2];
        
        /// <summary>
        /// Lowpass filter state variables for stage 2, z2 (per channel).
        /// </summary>
        private float[] _lpZ2_2 = new float[2];
        
        /// <summary>
        /// Highpass filter state variables for stage 1, z1 (per channel).
        /// </summary>
        private float[] _hpZ1_1 = new float[2];
        
        /// <summary>
        /// Highpass filter state variables for stage 1, z2 (per channel).
        /// </summary>
        private float[] _hpZ2_1 = new float[2];
        
        /// <summary>
        /// Highpass filter state variables for stage 2, z1 (per channel).
        /// </summary>
        private float[] _hpZ1_2 = new float[2];
        
        /// <summary>
        /// Highpass filter state variables for stage 2, z2 (per channel).
        /// </summary>
        private float[] _hpZ2_2 = new float[2];
        
        /// <summary>
        /// Initializes a new instance of the <see cref="CrossoverFilter"/> class.
        /// </summary>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="crossoverFreq">Crossover frequency in Hz.</param>
        public CrossoverFilter(float sampleRate, float crossoverFreq)
        {
            _sampleRate = sampleRate;
            _crossoverFreq = crossoverFreq;
            CalculateCoefficients();
        }
        
        /// <summary>
        /// Sets the crossover frequency and recalculates filter coefficients if changed.
        /// </summary>
        /// <param name="freq">New crossover frequency in Hz.</param>
        public void SetCrossoverFrequency(float freq)
        {
            // Only reset if frequency actually changes
            if (Math.Abs(_crossoverFreq - freq) > 0.01f)
            {
                _crossoverFreq = freq;
                CalculateCoefficients();
                Reset(); // Clear state when coefficients change
            }
        }
        
        private void CalculateCoefficients()
        {
            float omega = 2.0f * MathF.PI * _crossoverFreq / _sampleRate;
            float sinOmega = MathF.Sin(omega);
            float cosOmega = MathF.Cos(omega);
            float alpha = sinOmega / (2.0f * 0.707f); // Q = 0.707 (Butterworth)
            
            // Lowpass coefficients
            float lpB0 = (1.0f - cosOmega) / 2.0f;
            float lpB1 = 1.0f - cosOmega;
            float lpB2 = (1.0f - cosOmega) / 2.0f;
            float a0 = 1.0f + alpha;
            float a1 = -2.0f * cosOmega;
            float a2 = 1.0f - alpha;
            
            // Normalization (same for both cascades)
            _lpB0_1 = _lpB0_2 = lpB0 / a0;
            _lpB1_1 = _lpB1_2 = lpB1 / a0;
            _lpB2_1 = _lpB2_2 = lpB2 / a0;
            _lpA1_1 = _lpA1_2 = a1 / a0;
            _lpA2_1 = _lpA2_2 = a2 / a0;
            
            // Highpass coefficients
            float hpB0 = (1.0f + cosOmega) / 2.0f;
            float hpB1 = -(1.0f + cosOmega);
            float hpB2 = (1.0f + cosOmega) / 2.0f;
            
            // Normalization (same for both cascades)
            _hpB0_1 = _hpB0_2 = hpB0 / a0;
            _hpB1_1 = _hpB1_2 = hpB1 / a0;
            _hpB2_1 = _hpB2_2 = hpB2 / a0;
            _hpA1_1 = _hpA1_2 = a1 / a0;
            _hpA2_1 = _hpA2_2 = a2 / a0;
        }
        
        /// <summary>
        /// Processes audio through the crossover filter, splitting into high and low frequency bands.
        /// </summary>
        /// <param name="input">Input audio samples.</param>
        /// <param name="outputLR">Output buffer for high frequencies (L/R).</param>
        /// <param name="outputSub">Output buffer for low frequencies (Sub).</param>
        /// <param name="frameCount">Number of frames to process.</param>
        /// <param name="channel">Channel index (0 = left, 1 = right).</param>
        public void Process(Span<float> input, Span<float> outputLR, Span<float> outputSub, int frameCount, int channel = 0)
        {
            // Clamp channel to valid range
            channel = Math.Clamp(channel, 0, 1);
            
            // Local variables for faster access (channel-specific)
            float lpZ1_1 = _lpZ1_1[channel], lpZ2_1 = _lpZ2_1[channel];
            float lpZ1_2 = _lpZ1_2[channel], lpZ2_2 = _lpZ2_2[channel];
            float hpZ1_1 = _hpZ1_1[channel], hpZ2_1 = _hpZ2_1[channel];
            float hpZ1_2 = _hpZ1_2[channel], hpZ2_2 = _hpZ2_2[channel];
            
            // SAFETY: Check for NaN/Inf corruption in state variables - If detected, reset to zero to prevent audio corruption
            if (!float.IsFinite(lpZ1_1) || !float.IsFinite(lpZ2_1) || !float.IsFinite(lpZ1_2) || !float.IsFinite(lpZ2_2) ||
                !float.IsFinite(hpZ1_1) || !float.IsFinite(hpZ2_1) || !float.IsFinite(hpZ1_2) || !float.IsFinite(hpZ2_2))
            {
                lpZ1_1 = lpZ2_1 = lpZ1_2 = lpZ2_2 = 0f;
                hpZ1_1 = hpZ2_1 = hpZ1_2 = hpZ2_2 = 0f;
            }
            
            for (int i = 0; i < frameCount; i++)
            {
                float sample = input[i];
                
                // Lowpass (Sub) - First cascade
                float lpOut1 = _lpB0_1 * sample + lpZ1_1;
                lpZ1_1 = _lpB1_1 * sample - _lpA1_1 * lpOut1 + lpZ2_1;
                lpZ2_1 = _lpB2_1 * sample - _lpA2_1 * lpOut1;
                
                // Lowpass (Sub) - Second cascade
                float lpOut2 = _lpB0_2 * lpOut1 + lpZ1_2;
                lpZ1_2 = _lpB1_2 * lpOut1 - _lpA1_2 * lpOut2 + lpZ2_2;
                lpZ2_2 = _lpB2_2 * lpOut1 - _lpA2_2 * lpOut2;
                
                outputSub[i] = lpOut2;
                
                // Highpass (L/R) - First cascade
                float hpOut1 = _hpB0_1 * sample + hpZ1_1;
                hpZ1_1 = _hpB1_1 * sample - _hpA1_1 * hpOut1 + hpZ2_1;
                hpZ2_1 = _hpB2_1 * sample - _hpA2_1 * hpOut1;
                
                // Highpass (L/R) - Second cascade
                float hpOut2 = _hpB0_2 * hpOut1 + hpZ1_2;
                hpZ1_2 = _hpB1_2 * hpOut1 - _hpA1_2 * hpOut2 + hpZ2_2;
                hpZ2_2 = _hpB2_2 * hpOut1 - _hpA2_2 * hpOut2;
                
                outputLR[i] = hpOut2;
            }
            
            // Save states (channel-specific)
            _lpZ1_1[channel] = lpZ1_1; _lpZ2_1[channel] = lpZ2_1;
            _lpZ1_2[channel] = lpZ1_2; _lpZ2_2[channel] = lpZ2_2;
            _hpZ1_1[channel] = hpZ1_1; _hpZ2_1[channel] = hpZ2_1;
            _hpZ1_2[channel] = hpZ1_2; _hpZ2_2[channel] = hpZ2_2;
        }
        
        /// <summary>
        /// Resets all filter state variables to zero.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_lpZ1_1, 0, 2);
            Array.Clear(_lpZ2_1, 0, 2);
            Array.Clear(_lpZ1_2, 0, 2);
            Array.Clear(_lpZ2_2, 0, 2);
            Array.Clear(_hpZ1_1, 0, 2);
            Array.Clear(_hpZ2_1, 0, 2);
            Array.Clear(_hpZ1_2, 0, 2);
            Array.Clear(_hpZ2_2, 0, 2);
        }
    }
}
