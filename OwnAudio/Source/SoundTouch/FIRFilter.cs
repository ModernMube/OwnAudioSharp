// License :
//
// SoundTouch audio processing library
// Copyright (c) Olli Parviainen
// C# port Copyright (c) Olaf Woudenberg
//
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
//
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

namespace SoundTouch
{
    using System;
    using System.Diagnostics;
    using System.Buffers;
    using System.Runtime.InteropServices;
    using System.Runtime.Intrinsics;
    using System.Runtime.Intrinsics.X86;

    using SoundTouch.Assets;

    internal class FirFilter : IDisposable
    {
        // Memory for filter coefficients
        private float[]? _filterCoeffs;
        private float[]? _filterCoeffsStereo;

        public FirFilter()
        {
            Length = 0;
            _filterCoeffs = null;
            _filterCoeffsStereo = null;
        }

        public void Dispose()
        {
            if (_filterCoeffs != null)
            {
                ArrayPool<float>.Shared.Return(_filterCoeffs);
                _filterCoeffs = null;
            }
            if (_filterCoeffsStereo != null)
            {
                ArrayPool<float>.Shared.Return(_filterCoeffsStereo);
                _filterCoeffsStereo = null;
            }
        }

        // Number of FIR filter taps
        public int Length { get; private set; }

        public virtual void SetCoefficients(in ReadOnlySpan<float> coeffs, int resultDivFactor)
        {
            if (coeffs.IsEmpty)
                throw new ArgumentException(Strings.Argument_EmptyCoefficients, nameof(coeffs));
            if ((coeffs.Length % 8) != 0)
                throw new ArgumentException(Strings.Argument_CoefficientsFilterNotDivisible, nameof(coeffs));

            Length = coeffs.Length;

            // Result divider factor in 2^k format
            var resultDivider = (float)Math.Pow(2.0, resultDivFactor);

            double scale = 1.0 / resultDivider;

            // Return old buffers if they exist
            if (_filterCoeffs != null) ArrayPool<float>.Shared.Return(_filterCoeffs);
            if (_filterCoeffsStereo != null) ArrayPool<float>.Shared.Return(_filterCoeffsStereo);

            _filterCoeffs = ArrayPool<float>.Shared.Rent(Length);
            _filterCoeffsStereo = ArrayPool<float>.Shared.Rent(Length * 2);

            for (int i = 0; i < Length; i++)
            {
                _filterCoeffs[i] = (float)(coeffs[i] * scale);

                // create also stereo set of filter coefficients: this allows compiler
                // to autovectorize filter evaluation much more efficiently
                _filterCoeffsStereo[2 * i] = (float)(coeffs[i] * scale);
                _filterCoeffsStereo[(2 * i) + 1] = (float)(coeffs[i] * scale);
            }
        }

        /// <summary>
        /// Applies the filter to the given sequence of samples.
        /// Note : The amount of outputted samples is by value of 'filter_length'
        /// smaller than the amount of input samples.
        /// </summary>
        /// <returns>Number of samples copied to <paramref name="dest"/>.</returns>
        public int Evaluate(in Span<float> dest, in ReadOnlySpan<float> src, int numSamples, int numChannels)
        {
            if (Length <= 0)
                throw new InvalidOperationException(Strings.InvalidOperation_CoefficientsNotInitialized);

            if (numSamples < Length)
                return 0;

#if !USE_MULTICH_ALWAYS
            if (numChannels == 1)
            {
                return EvaluateFilterMono(dest, src, numSamples);
            }

            if (numChannels == 2)
            {
                return EvaluateFilterStereo(dest, src, numSamples);
            }
#endif // USE_MULTICH_ALWAYS
            Debug.Assert(numChannels > 0, "Multiple channels");
            return EvaluateFilterMulti(dest, src, numSamples, numChannels);
        }

        protected virtual int EvaluateFilterStereo(in Span<float> dest, in ReadOnlySpan<float> src, int numSamples)
        {
            if (Length <= 0 || _filterCoeffsStereo is null)
                throw new InvalidOperationException(Strings.InvalidOperation_CoefficientsNotInitialized);

            // hint compiler autovectorization that loop length is divisible by 8
            int ilength = Length & -8;

            var end = 2 * (numSamples - ilength);
            
            // NOTE: src is stereo interleaved: L R L R
            // _filterCoeffsStereo is: C0 C0 C1 C1 ... (duplicated for L and R)
            // Operation:
            // Dest[j] (L) = Sum(Src[j+2*k] * Coeff[2*k])
            // Dest[j+1] (R) = Sum(Src[j+2*k+1] * Coeff[2*k+1])
            //
            // Since Coeff[2*k] == Coeff[2*k+1], we are basically doing a dot product 
            // of the source vector and the coeff vector.

            int j = 0;

            ReadOnlySpan<float> coeffs = _filterCoeffsStereo.AsSpan(0, ilength * 2);

            for (; j < end; j += 2)
            {
                double sumLeft = 0, sumRight = 0;
                
                // AVX2 Implementation
                if (Avx.IsSupported && ilength >= 4) // Process 4 taps (8 floats) per iter
                {
                    int i = 0;
                    Vector256<float> vSum = Vector256<float>.Zero;
                    int iLimit = (ilength * 2) & -8; // ensure multiple of 8 floats

                    ReadOnlySpan<float> ptr = src.Slice(j);

                    for (; i < iLimit; i += 8)
                    {
                        var vSrc = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(ptr.Slice(i)));
                        var vCoeff = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(coeffs.Slice(i)));
                        vSum = Avx.Add(vSum, Avx.Multiply(vSrc, vCoeff));
                    }
                    
                    // Reduce vSum (L0, R0, L1, R1, L2, R2, L3, R3) -> (SumL, SumR)
                    
                    Vector128<float> vSum128 = Sse.Add(vSum.GetLower(), vSum.GetUpper()); // (L0+L2, R0+R2, L1+L3, R1+R3)
                    
                    // We need to add the upper half (L1+L3, R1+R3) to the lower half (L0+L2, R0+R2)
                    Vector128<float> vHigh = Sse.MoveHighToLow(vSum128, vSum128); 
                    Vector128<float> vFinal = Sse.Add(vSum128, vHigh); 
                    
                    sumLeft += vFinal.GetElement(0);
                    sumRight += vFinal.GetElement(1);

                    // Scalar remainder for i loop
                    for (int k = i/2; k < ilength; k++) 
                    {
                        // i was index in float array (stereo), k is index in taps
                        sumLeft += ptr[2 * k] * coeffs[2 * k];
                        sumRight += ptr[(2 * k) + 1] * coeffs[(2 * k) + 1];
                    }
                }
                else if (Sse.IsSupported && ilength >= 2)
                {
                    int i = 0;
                    Vector128<float> vSum = Vector128<float>.Zero;
                    int iLimit = (ilength * 2) & -4;

                    ReadOnlySpan<float> ptr = src.Slice(j);

                    for (; i < iLimit; i += 4)
                    {
                        var vSrc = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(ptr.Slice(i)));
                        var vCoeff = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(coeffs.Slice(i)));
                        vSum = Sse.Add(vSum, Sse.Multiply(vSrc, vCoeff));
                    }
                    
                    // vSum = (L0, R0, L1, R1)
                    // Move (L1, R1) down to add to (L0, R0)
                    Vector128<float> vHigh = Sse.MoveHighToLow(vSum, vSum);
                    Vector128<float> vFinal = Sse.Add(vSum, vHigh);
                    
                    sumLeft += vFinal.GetElement(0);
                    sumRight += vFinal.GetElement(1);
                    
                    for (int k = i/2; k < ilength; k++)
                    {
                        sumLeft += ptr[2 * k] * coeffs[2 * k];
                        sumRight += ptr[(2 * k) + 1] * coeffs[(2 * k) + 1];
                    }
                }
                else
                {
                    // Scalar fallback
                    ReadOnlySpan<float> ptr = src.Slice(j);
                    for (int i = 0; i < ilength; i++)
                    {
                        sumLeft += ptr[2 * i] * coeffs[2 * i];
                        sumRight += ptr[(2 * i) + 1] * coeffs[(2 * i) + 1];
                    }
                }

                dest[j] = (float)sumLeft;
                dest[j + 1] = (float)sumRight;
            }

            return numSamples - ilength;
        }

        protected virtual int EvaluateFilterMono(in Span<float> dest, in ReadOnlySpan<float> src, int numSamples)
        {
            if (Length <= 0 || _filterCoeffs is null)
                throw new InvalidOperationException(Strings.InvalidOperation_CoefficientsNotInitialized);

            // hint compiler autovectorization that loop length is divisible by 8
            int ilength = Length & -8;
            var end = numSamples - ilength;
            int j = 0;

            ReadOnlySpan<float> coeffs = _filterCoeffs.AsSpan(0, ilength);

            for (; j < end; j++)
            {
                double sum = 0;
                
                if (Avx.IsSupported && ilength >= 8)
                {
                    int i = 0;
                    Vector256<float> vSum = Vector256<float>.Zero;
                    ReadOnlySpan<float> pSrc = src.Slice(j);
                    
                    for (; i < ilength; i += 8)
                    {
                        var vS = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(pSrc.Slice(i)));
                        var vC = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(coeffs.Slice(i)));
                        vSum = Avx.Add(vSum, Avx.Multiply(vS, vC));
                    }
                    
                    Vector128<float> vSum128 = Sse.Add(vSum.GetLower(), vSum.GetUpper());
                    if (Sse3.IsSupported)
                    {
                        vSum128 = Sse3.HorizontalAdd(vSum128, vSum128);
                        vSum128 = Sse3.HorizontalAdd(vSum128, vSum128);
                        sum += vSum128.GetElement(0);
                    }
                    else
                    {
                        sum += vSum128.GetElement(0) + vSum128.GetElement(1) + vSum128.GetElement(2) + vSum128.GetElement(3);
                    }
                }
                else if (Sse.IsSupported && ilength >= 4)
                {
                    int i = 0;
                    Vector128<float> vSum = Vector128<float>.Zero;
                    ReadOnlySpan<float> pSrc = src.Slice(j);

                    for (; i < ilength; i += 4)
                    {
                        var vS = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(pSrc.Slice(i)));
                        var vC = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(coeffs.Slice(i)));
                        vSum = Sse.Add(vSum, Sse.Multiply(vS, vC));
                    }
                    
                    if (Sse3.IsSupported)
                    {
                        vSum = Sse3.HorizontalAdd(vSum, vSum);
                        vSum = Sse3.HorizontalAdd(vSum, vSum);
                        sum += vSum.GetElement(0);
                    }
                    else
                    {
                        sum += vSum.GetElement(0) + vSum.GetElement(1) + vSum.GetElement(2) + vSum.GetElement(3);
                    }
                }
                else
                {
                    ReadOnlySpan<float> pSrc = src.Slice(j);
                    for (int i = 0; i < ilength; i++)
                    {
                        sum += pSrc[i] * coeffs[i];
                    }
                }

                dest[j] = (float)sum;
            }

            return end;
        }

        protected virtual int EvaluateFilterMulti(in Span<float> dest, in ReadOnlySpan<float> src, int numSamples, int numChannels)
        {
            if (numChannels >= 16)
                throw new ArgumentOutOfRangeException(Strings.Argument_IllegalNumberOfChannels);
            if (Length <= 0 || _filterCoeffs is null)
                throw new InvalidOperationException(Strings.InvalidOperation_CoefficientsNotInitialized);

            // hint compiler autovectorization that loop length is divisible by 8
            int ilength = Length & -8;

            int end = numChannels * (numSamples - ilength);

            // Generic loop, harder to SIMD optimize efficiently without specific channel count
            // But we can still use pooling benefits.
            
            Span<double> sums = stackalloc double[16];

            for (int j = 0; j < end; j += numChannels)
            {
                ReadOnlySpan<float> ptr;
                int c, i;

                for (c = 0; c < numChannels; c++)
                {
                    sums[c] = 0;
                }

                ptr = src.Slice(j);

                for (i = 0; i < ilength; i++)
                {
                    float coef = _filterCoeffs[i];
                    for (c = 0; c < numChannels; c++)
                    {
                        sums[c] += ptr[0] * coef;
                        ptr = ptr.Slice(1);
                    }
                }

                for (c = 0; c < numChannels; c++)
                {
                    dest[j + c] = (float)sums[c];
                }
            }

            return numSamples - ilength;
        }
    }
}