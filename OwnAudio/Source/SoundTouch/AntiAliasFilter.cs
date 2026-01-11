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
#if !NETSTANDARD1_1
    using System.IO;
#endif

    //using JetBrains.Annotations;

    // define this to save AA filter coefficients to a file
    // #define _DEBUG_SAVE_AAFILTER_COEFFICIENTS 1

    /// <summary>
    /// Anti-alias filter is used to prevent folding of high frequencies when
    /// transposing the sample rate with interpolation.
    /// </summary>
    internal sealed class AntiAliasFilter : IDisposable
    {
        private const double PI = Math.PI;
        private const double DOUBLE_PI = Math.PI * 2;

        private readonly FirFilter _firFilter;

        // Low-pass filter cut-off frequency, negative = invalid
        private double _cutoffFreq;

        // num of filter taps
        private int _length;

        /// <summary>
        /// Initializes a new instance of the <see cref="AntiAliasFilter"/> class.
        /// </summary>
        public AntiAliasFilter(int length)
        {
            _firFilter = new FirFilter();
            _cutoffFreq = 0.5;
            Length = length;
        }

        public void Dispose()
        {
            _firFilter.Dispose();
        }

        /// <summary>
        /// Gets or sets the number of FIR filter taps, i.e. ~filter complexity.
        /// </summary>
        public int Length
        {
            get => _length;
            set
            {
                _length = value;
                CalculateCoefficients();
            }
        }

        /// <summary>
        /// Sets new anti-alias filter cut-off edge frequency, scaled to sampling
        /// frequency (nyquist frequency = 0.5). The filter will cut off the
        /// frequencies than that.
        /// </summary>
        public void SetCutoffFreq(double newCutoffFreq)
        {
            _cutoffFreq = newCutoffFreq;
            CalculateCoefficients();
        }

        /// <summary>
        /// Applies the filter to the given sequence of samples.
        /// </summary>
        /// <remarks>
        /// The amount of outputted samples is by value of 'filter length'
        /// smaller than the amount of input samples.
        /// </remarks>
        public int Evaluate(Span<float> dest, in ReadOnlySpan<float> src, int numSamples, int numChannels)
            => _firFilter.Evaluate(dest, src, numSamples, numChannels);

        /// <summary>
        /// Applies the filter to the given src &amp; dest pipes, so that processed amount of
        /// samples get removed from src, and produced amount added to dest.
        /// </summary>
        /// <remarks>
        /// The amount of outputted samples is by value of 'filter length'
        /// smaller than the amount of input samples.
        /// </remarks>
        public int Evaluate(in FifoSampleBuffer destinationBuffer, FifoSampleBuffer sourceBuffer)
        {
            if (sourceBuffer is null)
                throw new ArgumentNullException(nameof(sourceBuffer));
            if (destinationBuffer is null)
                throw new ArgumentNullException(nameof(destinationBuffer));

            var numChannels = sourceBuffer.Channels;

            Debug.Assert(numChannels == destinationBuffer.Channels, "Source and destination buffers should have same number of channels");

            var numSrcSamples = sourceBuffer.AvailableSamples;
            var source = sourceBuffer.PtrBegin();
            var destination = destinationBuffer.PtrEnd(numSrcSamples);
            var result = _firFilter.Evaluate(destination, source, numSrcSamples, numChannels);
            sourceBuffer.ReceiveSamples(result);
            destinationBuffer.PutSamples(result);

            return result;
        }

        [Conditional("_DEBUG_SAVE_AAFILTER_COEFFICIENTS")]
        private static void DebugSaveAntiAliasFilterCoefficients(in ReadOnlySpan<float> coefficients)
        {
#if !NETSTANDARD1_1
            using var file = File.Open("aa_filter_coeffs.txt", FileMode.Truncate);
            using var writer = new StreamWriter(file);

            foreach (var temp in coefficients)
            {
                writer.WriteLine(temp);
            }

#else
            Debug.WriteLine("Dumping the contents of the filter coefficients:");
            foreach (var temp in coefficients)
            {
                Debug.WriteLine(temp);
            }
#endif
        }

        /// <summary>
        /// Calculate the FIR coefficients realizing the given cutoff-frequency.
        /// </summary>
        private void CalculateCoefficients()
        {
            double temp;

            Debug.Assert(_length >= 2, "Length is larger than 2");
            Debug.Assert(_length % 4 == 0, "Length is dividable by 4");
            Debug.Assert(_cutoffFreq >= 0, "Cut-off frequency is between 0 and 0.5");
            Debug.Assert(_cutoffFreq <= 0.5, "Cut-off frequency is between 0 and 0.5");

            Span<double> work = stackalloc double[_length];
            Span<float> coefficients = stackalloc float[_length];

            var wc = 2.0 * PI * _cutoffFreq;
            var tempCoefficient = DOUBLE_PI / _length;

            double sum = 0;
            for (int i = 0; i < _length; i++)
            {
                var cntTemp = i - (_length / 2.0);

                temp = cntTemp * wc;
                double h;

                if (temp != 0)
                {
                    h = Math.Sin(temp) / temp; // sinc-function
                }
                else
                {
                    h = 1.0;
                }

                var w = 0.54 + (0.46 * Math.Cos(tempCoefficient * cntTemp));

                temp = w * h;
                work[i] = temp;

                // calc net sum of coefficients
                sum += temp;
            }

            Debug.Assert(sum > 0, "Ensure the sum of coefficients is larger than zero");

            Debug.Assert(work[_length / 2] > 0, "ensure we've really designed a low-pass filter...");
            Debug.Assert(work[(_length / 2) + 1] > -1e-6, "ensure we've really designed a low-pass filter...");
            Debug.Assert(work[(_length / 2) - 1] > -1e-6, "ensure we've really designed a low-pass filter...");

            // Calculate a scaling coefficient in such a way that the result can be
            // divided by 16384
            var scaleCoefficient = 16384.0f / sum;

            for (int i = 0; i < _length; i++)
            {
                temp = work[i] * scaleCoefficient;

                // scale & round to nearest integer
                temp += (temp >= 0) ? 0.5 : -0.5;

                Debug.Assert(temp >= -32768 && temp <= 32767, "ensure no overflows");
                coefficients[i] = (float)temp;
            }

            // Set coefficients. Use divide factor 14 => divide result by 2^14 = 16384
            _firFilter.SetCoefficients(coefficients, 14);

            DebugSaveAntiAliasFilterCoefficients(coefficients);
        }
    }
}
