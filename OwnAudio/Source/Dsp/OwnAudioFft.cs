using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Dsp;

/// <summary>
/// AOT-compatible, pure-managed FFT.
/// Power-of-two sizes use the Cooley-Tukey radix-2 DIT algorithm.
/// Arbitrary sizes (e.g. NFft=6144) use Bluestein's Chirp-Z algorithm,
/// which internally pads to the next power of two.
/// Both Forward and Inverse are unnormalized (matching FourierOptions.NoScaling).
/// </summary>
public static class OwnAudioFft
{
    #region Public Transform Methods

    /// <summary>
    /// In-place forward FFT. Handles any positive length (not just powers of two).
    /// </summary>
    public static void Forward(Span<Complex> data)
    {
        if (!IsPowerOfTwo(data.Length))
        {
            BluesteinForward(data);
            return;
        }
        BitReversePermute(data);
        ButterflyStages(data);
    }

    /// <summary>
    /// In-place inverse FFT (unnormalized). Callers must divide by N afterward.
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
    private static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    private static int NextPowerOfTwo(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BitReversePermute(Span<Complex> data)
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

            if (i < j)
                (data[i], data[j]) = (data[j], data[i]);
        }
    }

    private static void ButterflyStages(Span<Complex> data)
    {
        int n = data.Length;
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            Complex wLen = new(Math.Cos(angle), Math.Sin(angle));

            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                int half = len >> 1;

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

    // One cached state per thread; recreated only when N changes.
    [ThreadStatic]
    private static BluesteinState? _bluesteinState;

    private static void BluesteinForward(Span<Complex> data)
    {
        int n = data.Length;
        BluesteinState state = GetOrCreateBluesteinState(n);
        Complex[] a = state.A;

        // a[k] = x[k] * W[k],  W[k] = exp(-πi·k²/N)
        for (int k = 0; k < n; k++)
            a[k] = data[k] * state.Chirp[k];

        // Zero the padding region (reuse buffer: must clear tail each call)
        Array.Clear(a, n, state.M - n);

        // FFT of a (power-of-2 size M)
        BitReversePermute(a.AsSpan(0, state.M));
        ButterflyStages(a.AsSpan(0, state.M));

        // Pointwise multiply: a *= BExtFft  (BExtFft is pre-computed FFT of h_ext)
        Complex[] bFft = state.BExtFft;
        for (int k = 0; k < state.M; k++)
            a[k] *= bFft[k];

        // IFFT via conjugate-forward-conjugate, then divide by M
        for (int k = 0; k < state.M; k++) a[k] = Complex.Conjugate(a[k]);
        BitReversePermute(a.AsSpan(0, state.M));
        ButterflyStages(a.AsSpan(0, state.M));
        double invM = 1.0 / state.M;
        for (int k = 0; k < state.M; k++) a[k] = Complex.Conjugate(a[k]) * invM;

        // DFT[k] = W[k] · c[k]
        for (int k = 0; k < n; k++)
            data[k] = state.Chirp[k] * a[k];
    }

    private static BluesteinState GetOrCreateBluesteinState(int n)
    {
        if (_bluesteinState == null || _bluesteinState.N != n)
            _bluesteinState = new BluesteinState(n);
        return _bluesteinState;
    }

    /// <summary>
    /// Pre-computed and cached data for a specific non-power-of-2 FFT size N.
    /// </summary>
    private sealed class BluesteinState
    {
        public readonly int N;
        public readonly int M;           // next power of 2 >= 2N-1
        public readonly Complex[] Chirp; // W[k] = exp(-πi·k²/N), length N
        public readonly Complex[] A;     // reusable work buffer, length M
        public readonly Complex[] BExtFft; // pre-computed FFT of h_ext, length M

        public BluesteinState(int n)
        {
            N = n;
            M = NextPowerOfTwo(2 * n - 1);
            Chirp    = new Complex[n];
            A        = new Complex[M];
            BExtFft  = new Complex[M]; // zero-initialised

            // Chirp[k] = exp(-πi·k²/N)
            for (int k = 0; k < n; k++)
            {
                double theta = -Math.PI * k * k / n;
                Chirp[k] = new Complex(Math.Cos(theta), Math.Sin(theta));
            }

            // h[k] = conj(Chirp[k]) = exp(+πi·k²/N); h is even so h[-k]=h[k].
            // Lay out h_ext: h_ext[k]=h[k] for k=0..N-1,
            //                h_ext[M-k]=h[k] for k=1..N-1, rest zero.
            for (int k = 0; k < n; k++)
                BExtFft[k] = Complex.Conjugate(Chirp[k]);
            for (int k = 1; k < n; k++)
                BExtFft[M - k] = BExtFft[k];

            // Pre-compute FFT of h_ext (reused for every call with this N)
            BitReversePermute(BExtFft.AsSpan(0, M));
            ButterflyStages(BExtFft.AsSpan(0, M));
        }
    }

    #endregion
}
