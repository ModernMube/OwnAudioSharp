using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Dsp;

/// <summary>
/// AOT-compatible, pure-managed Cooley-Tukey radix-2 DIT FFT that replaces
/// MathNet.Numerics.IntegralTransforms.Fourier throughout the codebase.
/// Both Forward and Inverse are unnormalized (matching FourierOptions.NoScaling).
/// Spectrum analysis callers that previously used FourierOptions.Matlab only need
/// bin magnitudes, so the absence of 1/N pre-scaling has no effect on relative results.
/// </summary>
public static class OwnAudioFft
{
    #region Public Transform Methods

    /// <summary>
    /// In-place forward FFT (unnormalized). Input length must be a power of two.
    /// </summary>
    /// <param name="data">Complex samples; overwritten with frequency-domain output.</param>
    public static void Forward(Span<Complex> data)
    {
        int n = data.Length;
        BitReversePermute(data);

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

    /// <summary>
    /// In-place inverse FFT (unnormalized, no 1/N division). Input length must be a power of two.
    /// Callers that previously used FourierOptions.NoScaling must still divide by N afterward,
    /// exactly as they did with MathNet.
    /// </summary>
    /// <param name="data">Frequency-domain data; overwritten with time-domain output.</param>
    public static void Inverse(Span<Complex> data)
    {
        int n = data.Length;

        for (int i = 0; i < n; i++)
            data[i] = Complex.Conjugate(data[i]);

        Forward(data);

        for (int i = 0; i < n; i++)
            data[i] = Complex.Conjugate(data[i]);
    }

    /// <summary>
    /// In-place forward FFT operating on an array (convenience overload).
    /// </summary>
    /// <param name="data">Complex array; overwritten with frequency-domain output.</param>
    public static void Forward(Complex[] data) => Forward(data.AsSpan());

    /// <summary>
    /// In-place inverse FFT operating on an array (convenience overload).
    /// </summary>
    /// <param name="data">Complex array; overwritten with time-domain output.</param>
    public static void Inverse(Complex[] data) => Inverse(data.AsSpan());

    #endregion

    #region Private Helpers

    /// <summary>
    /// Bit-reversal permutation required by the Cooley-Tukey DIT algorithm.
    /// </summary>
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

    #endregion
}
