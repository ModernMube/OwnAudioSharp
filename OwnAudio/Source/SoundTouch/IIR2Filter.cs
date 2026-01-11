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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Reviewed: Acronim for Second order IIR filter.")]
    internal class IIR2Filter
    {
        private readonly double[] _coeffs/*[5]*/;
        private readonly double[] _prev/*[5]*/;

        public IIR2Filter(in Span<double> coeffs)
        {
            _coeffs = new double[5];
            _prev = new double[5];
            coeffs.CopyTo(_coeffs);
        }

        public float Update(float x)
        {
            _prev[0] = x;
            double y = x * _coeffs[0];

            for (int i = 4; i >= 1; i--)
            {
                y += _coeffs[i] * _prev[i];
                _prev[i] = _prev[i - 1];
            }

            _prev[3] = y;
            return (float)y;
        }
    }
}
