using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Dsp;

/// <summary>
/// Pure managed, AOT-safe FFT. Power-of-two lengths go through plain radix-2
/// Cooley-Tukey; anything else (NFft=6144 and friends) falls back to Bluestein,
/// which pads up to a power of two internally. Nothing is normalized here —
/// same deal as FourierOptions.NoScaling.
/// </summary>
public static class OwnAudioFft
{
    #region Public Transform Methods

    /// <summary>
    /// Forward FFT, in place, any positive length.
    /// </summary>
    public static void Forward(Span<Complex> data)
    {
        if (!_isPow2(data.Length))
        {
            _bluesteinForward(data);
            return;
        }
        _bitReversePermute(data);
        _butterflyStages(data);
    }

    /// <summary>
    /// Inverse FFT, in place and unnormalized — divide by N yourself afterwards.
    /// </summary>
    public static void Inverse(Span<Complex> data)
    {
        int n = data.Length;
        for (int i = 0; i < n; i++)
            data[i] = Complex.Conjugate(data[i]);

        Forward(data);

        for (int i = 0; i < n; i++)
            data[i] = Complex.Conjugate(data[i]);
    }

    public static void Forward(Complex[] data) => Forward(data.AsSpan());
    public static void Inverse(Complex[] data) => Inverse(data.AsSpan());

    #endregion

    #region Private — Power-of-2 helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _isPow2(int n) => n > 0 && (n & (n - 1)) == 0;

    private static int _nextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>
    /// Shuffles samples into bit-reversed order so the butterflies can run in place.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _bitReversePermute(Span<Complex> data)
    {
        int n = data.Length;
        int j = 0;

        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }
            j ^= bit;

            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
    }

    /// <summary>
    /// The actual radix-2 DIT passes, doubling the block length each round.
    /// </summary>
    private static void _butterflyStages(Span<Complex> data)
    {
        int n = data.Length;
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            Complex wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            int half = len >> 1;

            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;

                for (int j = 0; j < half; j++)
                {
                    Complex u = data[i + j];
                    Complex v = data[i + j + half] * w;
                    data[i + j]        = u + v;
                    data[i + j + half] = u - v;
                    w *= wLen;
                }
            }
        }
    }

    #endregion

    #region Private — Bluestein (Chirp-Z) for arbitrary N

    /// <summary>
    /// One cached state per thread, rebuilt only when N changes.
    /// </summary>
    [ThreadStatic]
    private static BluesteinState? _bluesteinState;

    private static void _bluesteinForward(Span<Complex> data)
    {
        int n = data.Length;
        BluesteinState state = _getBluesteinState(n);
        Complex[] a = state.A;
        int m = state.M;

        for (int k = 0; k < n; k++)
            a[k] = data[k] * state.Chirp[k];

        Array.Clear(a, n, m - n);

        _bitReversePermute(a.AsSpan(0, m));
        _butterflyStages(a.AsSpan(0, m));

        Complex[] bFft = state.BExtFft;
        for (int k = 0; k < m; k++)
            a[k] *= bFft[k];

        double invM = 1.0 / m;
        for (int k = 0; k < m; k++) a[k] = Complex.Conjugate(a[k]);
        _bitReversePermute(a.AsSpan(0, m));
        _butterflyStages(a.AsSpan(0, m));
        for (int k = 0; k < m; k++) a[k] = Complex.Conjugate(a[k]) * invM;

        for (int k = 0; k < n; k++)
            data[k] = state.Chirp[k] * a[k];
    }

    private static BluesteinState _getBluesteinState(int n)
    {
        if (_bluesteinState == null || _bluesteinState.N != n)
            _bluesteinState = new BluesteinState(n);
        return _bluesteinState;
    }

    /// <summary>
    /// Everything we can precompute for one particular non-power-of-2 size.
    /// Built once per N, then reused for every call on that thread.
    /// </summary>
    private sealed class BluesteinState
    {
        public readonly int N;

        /// <summary>
        /// Next power of two at or above 2N-1.
        /// </summary>
        public readonly int M;

        /// <summary>
        /// W[k] = exp(-pi·i·k^2/N), N long.
        /// </summary>
        public readonly Complex[] Chirp;

        /// <summary>
        /// Scratch we convolve in, M long. Reused, so the tail needs clearing per call.
        /// </summary>
        public readonly Complex[] A;

        /// <summary>
        /// FFT of the extended chirp kernel, M long.
        /// </summary>
        public readonly Complex[] BExtFft;

        public BluesteinState(int n)
        {
            N = n;
            M = _nextPow2(2 * n - 1);
            Chirp    = new Complex[n];
            A        = new Complex[M];
            BExtFft  = new Complex[M];

            for (int k = 0; k < n; k++)
            {
                double theta = -Math.PI * k * k / n;
                Chirp[k] = new Complex(Math.Cos(theta), Math.Sin(theta));
            }

            for (int k = 0; k < n; k++)
                BExtFft[k] = Complex.Conjugate(Chirp[k]);
            for (int k = 1; k < n; k++)
                BExtFft[M - k] = BExtFft[k];

            _bitReversePermute(BExtFft.AsSpan(0, M));
            _butterflyStages(BExtFft.AsSpan(0, M));
        }
    }

    #endregion
}
