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

    internal sealed class InterpolateShannon : TransposerBase
    {
        private const double PI = Math.PI;

        // Kaiser window with beta = 2.0
        // Values scaled down by 5% to avoid overflows
        private static readonly double[] _kaiser8 =
        {
           0.41778693317814,
           0.64888025049173,
           0.83508562409944,
           0.93887857733412,
           0.93887857733412,
           0.83508562409944,
           0.64888025049173,
           0.41778693317814
        };

        private double _fract;

        public InterpolateShannon()
        {
            _fract = 0;
        }

        public override int Latency => 3;

        public override void ResetRegisters() => _fract = 0;

        protected override int TransposeMono(in Span<float> dest, in ReadOnlySpan<float> src, ref int srcSamples)
        {
            var psrc = src;

            int i;
            int srcSampleEnd = srcSamples - 8;
            int srcCount = 0;

            i = 0;
            while (srcCount < srcSampleEnd)
            {
                double @out;
                Debug.Assert(_fract < 1.0, "_fract < 1.0");

                @out = psrc[0] * Sinc(-3.0 - _fract) * _kaiser8[0];
                @out += psrc[1] * Sinc(-2.0 - _fract) * _kaiser8[1];
                @out += psrc[2] * Sinc(-1.0 - _fract) * _kaiser8[2];
                if (_fract < 1e-6)
                {
                    @out += psrc[3] * _kaiser8[3];     // sinc(0) = 1
                }
                else
                {
                    @out += psrc[3] * Sinc(-_fract) * _kaiser8[3];
                }

                @out += psrc[4] * Sinc(1.0 - _fract) * _kaiser8[4];
                @out += psrc[5] * Sinc(2.0 - _fract) * _kaiser8[5];
                @out += psrc[6] * Sinc(3.0 - _fract) * _kaiser8[6];
                @out += psrc[7] * Sinc(4.0 - _fract) * _kaiser8[7];

                dest[i] = (float)@out;
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
            int srcSampleEnd = srcSamples - 8;
            int srcCount = 0;

            i = 0;
            while (srcCount < srcSampleEnd)
            {
                double outLeft, outRight, w;
                Debug.Assert(_fract < 1.0, "_fract < 1.0");

                w = Sinc(-3.0 - _fract) * _kaiser8[0];
                outLeft = psrc[0] * w;
                outRight = psrc[1] * w;
                w = Sinc(-2.0 - _fract) * _kaiser8[1];
                outLeft += psrc[2] * w;
                outRight += psrc[3] * w;
                w = Sinc(-1.0 - _fract) * _kaiser8[2];
                outLeft += psrc[4] * w;
                outRight += psrc[5] * w;
                w = _kaiser8[3] * ((_fract < 1e-5) ? 1.0 : Sinc(-_fract));   // sinc(0) = 1
                outLeft += psrc[6] * w;
                outRight += psrc[7] * w;
                w = Sinc(1.0 - _fract) * _kaiser8[4];
                outLeft += psrc[8] * w;
                outRight += psrc[9] * w;
                w = Sinc(2.0 - _fract) * _kaiser8[5];
                outLeft += psrc[10] * w;
                outRight += psrc[11] * w;
                w = Sinc(3.0 - _fract) * _kaiser8[6];
                outLeft += psrc[12] * w;
                outRight += psrc[13] * w;
                w = Sinc(4.0 - _fract) * _kaiser8[7];
                outLeft += psrc[14] * w;
                outRight += psrc[15] * w;

                dest[2 * i] = (float)outLeft;
                dest[(2 * i) + 1] = (float)outRight;
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
            throw new NotImplementedException();
        }

        private static double Sinc(double x) => Math.Sin(PI * x) / (PI * x);
    }
}
