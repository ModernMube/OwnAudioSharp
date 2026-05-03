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
