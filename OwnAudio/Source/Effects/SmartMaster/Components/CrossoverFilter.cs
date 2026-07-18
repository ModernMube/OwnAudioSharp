using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Splits the signal into L/R and Sub branches.
    /// Linkwitz-Riley 4th order, two Butterworth biquads cascaded.
    /// </summary>
    public class CrossoverFilter
    {
        private float _sampleRate;
        private float _crossoverFreq;

        /// <summary>
        /// Biquad coefficients, both cascades share the same values.
        /// </summary>
        private float _lpB0, _lpB1, _lpB2, _lpA1, _lpA2;
        private float _hpB0, _hpB1, _hpB2, _hpA1, _hpA2;

        /// <summary>
        /// Filter memory per channel, [stage][channel].
        /// </summary>
        private readonly float[,] _lpZ1 = new float[2, 2];
        private readonly float[,] _lpZ2 = new float[2, 2];
        private readonly float[,] _hpZ1 = new float[2, 2];
        private readonly float[,] _hpZ2 = new float[2, 2];

        /// <summary>
        /// Both frequencies in Hz.
        /// </summary>
        public CrossoverFilter(float sampleRate, float crossoverFreq)
        {
            _sampleRate = sampleRate;
            _crossoverFreq = crossoverFreq;
            _calcCoeffs();
        }

        /// <summary>
        /// New split point in Hz. State gets flushed if it really moved.
        /// </summary>
        /// <param name="freq"></param>
        public void SetCrossoverFrequency(float freq)
        {
            if (Math.Abs(_crossoverFreq - freq) <= 0.01f) return;

            _crossoverFreq = freq;
            _calcCoeffs();
            Reset();
        }

        private void _calcCoeffs()
        {
            float omega = 2.0f * MathF.PI * _crossoverFreq / _sampleRate;
            float cosOmega = MathF.Cos(omega);
            float alpha = MathF.Sin(omega) / (2.0f * 0.707f);

            float a0 = 1.0f + alpha;
            _lpA1 = _hpA1 = (-2.0f * cosOmega) / a0;
            _lpA2 = _hpA2 = (1.0f - alpha) / a0;

            _lpB0 = _lpB2 = ((1.0f - cosOmega) / 2.0f) / a0;
            _lpB1 = (1.0f - cosOmega) / a0;

            _hpB0 = _hpB2 = ((1.0f + cosOmega) / 2.0f) / a0;
            _hpB1 = -(1.0f + cosOmega) / a0;
        }

        /// <summary>
        /// Runs one channel through the split. outputLR gets the highs,
        /// outputSub the lows; channel is 0 = left, 1 = right.
        /// </summary>
        public void Process(Span<float> input, Span<float> outputLR, Span<float> outputSub, int frameCount, int channel = 0)
        {
            float lz1a = _lpZ1[0, channel], lz2a = _lpZ2[0, channel];
            float lz1b = _lpZ1[1, channel], lz2b = _lpZ2[1, channel];
            float hz1a = _hpZ1[0, channel], hz2a = _hpZ2[0, channel];
            float hz1b = _hpZ1[1, channel], hz2b = _hpZ2[1, channel];

            //Blown-up state would keep poisoning the output, so wipe it
            if (!float.IsFinite(lz1a + lz2a + lz1b + lz2b + hz1a + hz2a + hz1b + hz2b))
            {
                lz1a = lz2a = lz1b = lz2b = 0f;
                hz1a = hz2a = hz1b = hz2b = 0f;
            }

            for (int i = 0; i < frameCount; i++)
            {
                float s = input[i];

                float lo1 = _lpB0 * s + lz1a;
                lz1a = _lpB1 * s - _lpA1 * lo1 + lz2a;
                lz2a = _lpB2 * s - _lpA2 * lo1;

                float lo2 = _lpB0 * lo1 + lz1b;
                lz1b = _lpB1 * lo1 - _lpA1 * lo2 + lz2b;
                lz2b = _lpB2 * lo1 - _lpA2 * lo2;
                outputSub[i] = lo2;

                float hi1 = _hpB0 * s + hz1a;
                hz1a = _hpB1 * s - _hpA1 * hi1 + hz2a;
                hz2a = _hpB2 * s - _hpA2 * hi1;

                float hi2 = _hpB0 * hi1 + hz1b;
                hz1b = _hpB1 * hi1 - _hpA1 * hi2 + hz2b;
                hz2b = _hpB2 * hi1 - _hpA2 * hi2;
                outputLR[i] = hi2;
            }

            _lpZ1[0, channel] = lz1a; _lpZ2[0, channel] = lz2a;
            _lpZ1[1, channel] = lz1b; _lpZ2[1, channel] = lz2b;
            _hpZ1[0, channel] = hz1a; _hpZ2[0, channel] = hz2a;
            _hpZ1[1, channel] = hz1b; _hpZ2[1, channel] = hz2b;
        }

        /// <summary>
        /// Flushes every biquad's memory.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_lpZ1);
            Array.Clear(_lpZ2);
            Array.Clear(_hpZ1);
            Array.Clear(_hpZ2);
        }
    }
}
