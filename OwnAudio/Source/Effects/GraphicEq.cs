using System;
using System.Numerics;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Graphic EQ maths shared by the 10-band, the 30-band and the Smart Master's EQ.
    /// </summary>
    public static class GraphicEq
    {
        /// <summary>
        /// Magnitude of one RBJ peaking biquad at atHz, in dB — the exact digital response, not the analog sketch.
        /// </summary>
        public static double BellDb(double centreHz, double q, double gainDb, double atHz, double sampleRate)
        {
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2 * Math.PI * centreHz / sampleRate;
            double alpha = Math.Sin(w0) / (2 * q);
            double cos = Math.Cos(w0);

            Complex z1 = Complex.FromPolarCoordinates(1.0, -2 * Math.PI * atHz / sampleRate);
            Complex z2 = z1 * z1;

            Complex num = (1 + alpha * a) - 2 * cos * z1 + (1 - alpha * a) * z2;
            Complex den = (1 + alpha / a) - 2 * cos * z1 + (1 - alpha / a) * z2;

            return 20.0 * Math.Log10(Math.Max((num / den).Magnitude, 1e-12));
        }
    }

    /// <summary>
    /// Turns fader positions into filter gains. A fader is the curve at its band centre, but wide
    /// bells overlap, so the bank is solved against them — ridge regression, the ridge keeps a
    /// zigzag of faders from ringing into ±30 dB filters.
    /// </summary>
    internal sealed class GraphicEqSolver
    {
        private const float Snap = 0.02f;

        private readonly int _bands;
        private readonly int _stages;
        private readonly double _ridge;
        private readonly float _limit;
        private readonly float[] _k;

        /// <summary>
        /// stages is how many cascaded biquads share one band's gain (the 10-band runs two).
        /// </summary>
        public GraphicEqSolver(int bands, int stages, double ridge, float limitDb)
        {
            _bands = bands;
            _stages = stages;
            _ridge = ridge;
            _limit = limitDb;
            _k = new float[bands * bands];
        }

        public float TunedSampleRate { get; private set; }

        /// <summary>
        /// Rebuilds the solution matrix. Only on a shape or rate change, never per fader move.
        /// </summary>
        public void Retune(ReadOnlySpan<float> freqs, ReadOnlySpan<float> qs, float sampleRate)
        {
            int n = _bands;
            var m = new double[n, n];

            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    m[j, i] = _stages * GraphicEq.BellDb(freqs[i], qs[i], 1.0 / _stages, freqs[j], sampleRate);

            var a = new double[n, 2 * n];
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    double _sum = 0;
                    for (int k = 0; k < n; k++) _sum += m[k, r] * m[k, c];
                    a[r, c] = _sum + (r == c ? _ridge : 0.0);
                }
                a[r, n + r] = 1.0;
            }

            _invertInPlace(a, n);

            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    double _sum = 0;
                    for (int k = 0; k < n; k++) _sum += a[r, n + k] * m[c, k];
                    _k[r * n + c] = (float)_sum;
                }

            TunedSampleRate = sampleRate;
        }

        /// <summary>
        /// Filter gains for a curve. No allocation, safe to call on any thread that owns filter.
        /// </summary>
        public void Solve(ReadOnlySpan<float> curve, Span<float> filter)
        {
            int n = _bands;
            for (int r = 0; r < n; r++)
            {
                float _sum = 0f;
                for (int c = 0; c < n; c++) _sum += _k[r * n + c] * curve[c];

                if (MathF.Abs(_sum) < Snap) _sum = 0f;
                filter[r] = Math.Clamp(_sum, -_limit, _limit);
            }
        }

        private static void _invertInPlace(double[,] a, int n)
        {
            for (int p = 0; p < n; p++)
            {
                int _best = p;
                for (int r = p + 1; r < n; r++)
                    if (Math.Abs(a[r, p]) > Math.Abs(a[_best, p])) _best = r;

                if (_best != p)
                    for (int c = 0; c < 2 * n; c++) (a[p, c], a[_best, c]) = (a[_best, c], a[p, c]);

                double d = a[p, p];
                for (int c = 0; c < 2 * n; c++) a[p, c] /= d;

                for (int r = 0; r < n; r++)
                {
                    if (r == p) continue;
                    double f = a[r, p];
                    if (f == 0) continue;
                    for (int c = 0; c < 2 * n; c++) a[r, c] -= f * a[p, c];
                }
            }
        }
    }
}
