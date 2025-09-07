using System;

namespace Ownaudio.Utilities.Matchering
{
    #region Multiband Biquad Filter

    /// <summary>
    /// Biquad filter implementation with stability checking for multiband processing
    /// </summary>
    public class MultiBandBiquadFilter
    {
        /// <summary>
        /// Filter coefficients for the biquad equation
        /// </summary>
        private float a0, a1, a2, b0, b1, b2;

        /// <summary>
        /// Filter memory for input samples
        /// </summary>
        private float x1, x2;

        /// <summary>
        /// Filter memory for output samples
        /// </summary>
        private float y1, y2;

        /// <summary>
        /// Flag indicating whether the filter is stable
        /// </summary>
        private bool isStable = true;

        /// <summary>
        /// Initializes a new instance of the MultiBandBiquadFilter class
        /// </summary>
        /// <param name="frequency">Center or cutoff frequency in Hz</param>
        /// <param name="gainDb">Gain in decibels</param>
        /// <param name="q">Quality factor</param>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        /// <param name="type">Type of biquad filter</param>
        public MultiBandBiquadFilter(float frequency, float gainDb, float q, int sampleRate, BiquadType type)
        {
            CalculateCoefficients(frequency, gainDb, q, sampleRate, type);
            Reset();
        }

        /// <summary>
        /// Processes audio samples through the biquad filter
        /// </summary>
        /// <param name="samples">Audio samples to process in-place</param>
        public void Process(Span<float> samples)
        {
            if (!isStable)
            {
                return;
            }

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                float output = b0 * input + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;

                if (float.IsNaN(output) || float.IsInfinity(output) || Math.Abs(output) > 10.0f)
                {
                    output = input;
                    Reset();
                }

                x2 = x1;
                x1 = input;
                y2 = y1;
                y1 = output;

                samples[i] = output;
            }
        }

        /// <summary>
        /// Resets the filter's internal state
        /// </summary>
        public void Reset()
        {
            x1 = x2 = y1 = y2 = 0;
        }

        /// <summary>
        /// Calculates filter coefficients based on the specified parameters
        /// </summary>
        /// <param name="frequency">Center or cutoff frequency in Hz</param>
        /// <param name="gainDb">Gain in decibels</param>
        /// <param name="q">Quality factor</param>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        /// <param name="type">Filter type to calculate</param>
        private void CalculateCoefficients(float frequency, float gainDb, float q, int sampleRate, BiquadType type)
        {
            if (frequency <= 0 || frequency >= sampleRate * 0.48f || q <= 0 || sampleRate <= 0)
            {
                Console.WriteLine($"WARNING: Invalid parameters - freq:{frequency}, q:{q}, sr:{sampleRate}");
                SetBypassFilter();
                return;
            }

            gainDb = Math.Max(-60f, Math.Min(60f, gainDb));
            q = Math.Max(0.1f, Math.Min(20f, q));

            if (frequency > 8000f)
            {
                gainDb = Math.Max(-60f, Math.Min(3.0f, gainDb));
            }

            float w = 2.0f * (float)Math.PI * frequency / sampleRate;
            float cosw = (float)Math.Cos(w);
            float sinw = (float)Math.Sin(w);
            float A = (float)Math.Pow(10, gainDb / 40.0);
            float alpha = sinw / (2.0f * q);

            if (float.IsNaN(w) || float.IsInfinity(w) ||
                float.IsNaN(alpha) || float.IsInfinity(alpha) ||
                float.IsNaN(A) || float.IsInfinity(A))
            {
                Console.WriteLine($"WARNING: NaN/Infinity in calculations");
                SetBypassFilter();
                return;
            }

            switch (type)
            {
                case BiquadType.Lowpass:
                    b0 = (1 - cosw) / 2;
                    b1 = 1 - cosw;
                    b2 = (1 - cosw) / 2;
                    a0 = 1 + alpha;
                    a1 = -2 * cosw;
                    a2 = 1 - alpha;
                    break;

                case BiquadType.Highpass:
                    b0 = (1 + cosw) / 2;
                    b1 = -(1 + cosw);
                    b2 = (1 + cosw) / 2;
                    a0 = 1 + alpha;
                    a1 = -2 * cosw;
                    a2 = 1 - alpha;
                    break;

                case BiquadType.Bandpass:
                    b0 = sinw / 2;
                    b1 = 0;
                    b2 = -sinw / 2;
                    a0 = 1 + alpha;
                    a1 = -2 * cosw;
                    a2 = 1 - alpha;
                    break;

                case BiquadType.Notch:
                    b0 = 1;
                    b1 = -2 * cosw;
                    b2 = 1;
                    a0 = 1 + alpha;
                    a1 = -2 * cosw;
                    a2 = 1 - alpha;
                    break;

                case BiquadType.Peaking:
                    b0 = 1 + alpha * A;
                    b1 = -2 * cosw;
                    b2 = 1 - alpha * A;
                    a0 = 1 + alpha / A;
                    a1 = -2 * cosw;
                    a2 = 1 - alpha / A;
                    break;

                case BiquadType.LowShelf:
                    {
                        float beta = (float)Math.Sqrt(A) / q;

                        b0 = A * ((A + 1) - (A - 1) * cosw + beta * sinw);
                        b1 = 2 * A * ((A - 1) - (A + 1) * cosw);
                        b2 = A * ((A + 1) - (A - 1) * cosw - beta * sinw);
                        a0 = (A + 1) + (A - 1) * cosw + beta * sinw;
                        a1 = -2 * ((A - 1) + (A + 1) * cosw);
                        a2 = (A + 1) + (A - 1) * cosw - beta * sinw;
                    }
                    break;

                case BiquadType.HighShelf:
                    {
                        float beta = (float)Math.Sqrt(A) / q;

                        b0 = A * ((A + 1) + (A - 1) * cosw + beta * sinw);
                        b1 = -2 * A * ((A - 1) + (A + 1) * cosw);
                        b2 = A * ((A + 1) + (A - 1) * cosw - beta * sinw);
                        a0 = (A + 1) - (A - 1) * cosw + beta * sinw;
                        a1 = 2 * ((A - 1) - (A + 1) * cosw);
                        a2 = (A + 1) - (A - 1) * cosw - beta * sinw;
                    }
                    break;

                default:
                    SetBypassFilter();
                    return;
            }

            if (Math.Abs(a0) < 1e-6f)
            {
                Console.WriteLine($"WARNING: a0 too small: {a0} - setting bypass");
                SetBypassFilter();
                return;
            }

            b0 /= a0;
            b1 /= a0;
            b2 /= a0;
            a1 /= a0;
            a2 /= a0;
            a0 = 1.0f;

            float discriminant = a1 * a1 - 4 * a2;
            if (discriminant >= 0)
            {
                float pole1 = (-a1 + (float)Math.Sqrt(discriminant)) / 2;
                float pole2 = (-a1 - (float)Math.Sqrt(discriminant)) / 2;

                if (Math.Abs(pole1) >= 1.0f || Math.Abs(pole2) >= 1.0f)
                {
                    Console.WriteLine($"WARNING: Unstable filter - poles outside unit circle");
                    SetBypassFilter();
                    return;
                }
            }
            else
            {
                float realPart = -a1 / 2;
                float imagPart = (float)Math.Sqrt(-discriminant) / 2;
                float magnitude = (float)Math.Sqrt(realPart * realPart + imagPart * imagPart);

                if (magnitude >= 1.0f)
                {
                    Console.WriteLine($"WARNING: Unstable filter - complex poles outside unit circle");
                    SetBypassFilter();
                    return;
                }
            }

            isStable = true;
        }

        /// <summary>
        /// Sets the filter to bypass mode with unity gain
        /// </summary>
        private void SetBypassFilter()
        {
            b0 = 1.0f;
            b1 = 0.0f;
            b2 = 0.0f;
            a1 = 0.0f;
            a2 = 0.0f;
            isStable = false;
        }
    }

    #endregion
}
