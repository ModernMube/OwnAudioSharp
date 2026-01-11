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

    internal sealed class InterpolateCubic : TransposerBase
    {
        // cubic interpolation coefficients
        private static readonly float[] _coeffs =
        {
#pragma warning disable SA1137 // Elements should have the same indentation
          -0.5f,  1.0f, -0.5f, 0.0f,
           1.5f, -2.5f,  0.0f, 1.0f,
          -1.5f,  2.0f,  0.5f, 0.0f,
           0.5f, -0.5f,  0.0f, 0.0f
#pragma warning restore SA1137 // Elements should have the same indentation
        };

        private double _fract;

        public InterpolateCubic()
        {
            _fract = 0;
        }

        public override int Latency => 1;

        public override void ResetRegisters() => _fract = 0;

        protected override int TransposeMono(in Span<float> dest, in ReadOnlySpan<float> src, ref int srcSamples)
        {
            var psrc = src;

            int i;
            int srcSampleEnd = srcSamples - 4;
            int srcCount = 0;

            i = 0;
            while (srcCount < srcSampleEnd)
            {
                float output;
                float x3 = 1.0f;
                float x2 = (float)_fract;    // x
                float x1 = x2 * x2;         // x^2
                float x0 = x1 * x2;         // x^3
                float y0, y1, y2, y3;

                Debug.Assert(_fract < 1.0, "_fract < 1.0");

                y0 = (_coeffs[0] * x0) + (_coeffs[1] * x1) + (_coeffs[2] * x2) + (_coeffs[3] * x3);
                y1 = (_coeffs[4] * x0) + (_coeffs[5] * x1) + (_coeffs[6] * x2) + (_coeffs[7] * x3);
                y2 = (_coeffs[8] * x0) + (_coeffs[9] * x1) + (_coeffs[10] * x2) + (_coeffs[11] * x3);
                y3 = (_coeffs[12] * x0) + (_coeffs[13] * x1) + (_coeffs[14] * x2) + (_coeffs[15] * x3);

                output = (y0 * psrc[0]) + (y1 * psrc[1]) + (y2 * psrc[2]) + (y3 * psrc[3]);

                dest[i] = output;
                i++;

                // update position fraction
                _fract += Rate;

                // update whole positions
                int whole = (int)_fract;
                _fract -= whole;
                psrc = psrc.Slice(whole);
                srcCount += whole;
            }

            srcSamples = srcCount;
            return i;
        }

        protected override int TransposeStereo(in Span<float> dest, in ReadOnlySpan<float> src, ref int srcSamples)
        {
            var psrc = src;

            int i;
            int srcSampleEnd = srcSamples - 4;
            int srcCount = 0;

            i = 0;
            while (srcCount < srcSampleEnd)
            {
                float x3 = 1.0f;
                float x2 = (float)_fract;    // x
                float x1 = x2 * x2;           // x^2
                float x0 = x1 * x2;           // x^3
                float y0, y1, y2, y3;
                float outLeft, outRight;

                Debug.Assert(_fract < 1.0, "_fract < 1.0");

                y0 = (_coeffs[0] * x0) + (_coeffs[1] * x1) + (_coeffs[2] * x2) + (_coeffs[3] * x3);
                y1 = (_coeffs[4] * x0) + (_coeffs[5] * x1) + (_coeffs[6] * x2) + (_coeffs[7] * x3);
                y2 = (_coeffs[8] * x0) + (_coeffs[9] * x1) + (_coeffs[10] * x2) + (_coeffs[11] * x3);
                y3 = (_coeffs[12] * x0) + (_coeffs[13] * x1) + (_coeffs[14] * x2) + (_coeffs[15] * x3);

                outLeft = (y0 * psrc[0]) + (y1 * psrc[2]) + (y2 * psrc[4]) + (y3 * psrc[6]);
                outRight = (y0 * psrc[1]) + (y1 * psrc[3]) + (y2 * psrc[5]) + (y3 * psrc[7]);

                dest[2 * i] = outLeft;
                dest[(2 * i) + 1] = outRight;
                i++;

                // update position fraction
                _fract += Rate;

                // update whole positions
                int whole = (int)_fract;
                _fract -= whole;
                psrc = psrc.Slice(2 * whole);
                srcCount += whole;
            }

            srcSamples = srcCount;
            return i;
        }

        protected override int TransposeMulti(in Span<float> dest, in ReadOnlySpan<float> src, ref int srcSamples)
        {
            var psrc = src;
            var pdest = dest;

            int i;
            int srcSampleEnd = srcSamples - 4;
            int srcCount = 0;

            i = 0;
            while (srcCount < srcSampleEnd)
            {
                float x3 = 1.0f;
                float x2 = (float)_fract;    // x
                float x1 = x2 * x2;           // x^2
                float x0 = x1 * x2;           // x^3
                float y0, y1, y2, y3;

                Debug.Assert(_fract < 1.0, "_fract < 1.0");

                y0 = (_coeffs[0] * x0) + (_coeffs[1] * x1) + (_coeffs[2] * x2) + (_coeffs[3] * x3);
                y1 = (_coeffs[4] * x0) + (_coeffs[5] * x1) + (_coeffs[6] * x2) + (_coeffs[7] * x3);
                y2 = (_coeffs[8] * x0) + (_coeffs[9] * x1) + (_coeffs[10] * x2) + (_coeffs[11] * x3);
                y3 = (_coeffs[12] * x0) + (_coeffs[13] * x1) + (_coeffs[14] * x2) + (_coeffs[15] * x3);

                for (int c = 0; c < NumberOfChannels; c++)
                {
                    float output;
                    output = (y0 * psrc[c]) + (y1 * psrc[c + NumberOfChannels]) + (y2 * psrc[c + (2 * NumberOfChannels)]) + (y3 * psrc[c + (3 * NumberOfChannels)]);
                    pdest[0] = output;
                    pdest = pdest.Slice(1);
                }

                i++;

                // update position fraction
                _fract += Rate;

                // update whole positions
                int whole = (int)_fract;
                _fract -= whole;
                psrc = psrc.Slice(NumberOfChannels * whole);
                srcCount += whole;
            }

            srcSamples = srcCount;
            return i;
        }
    }
}
