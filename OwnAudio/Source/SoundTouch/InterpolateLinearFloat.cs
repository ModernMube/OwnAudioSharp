namespace SoundTouch
{
    using System;
    using System.Diagnostics;

    internal sealed class InterpolateLinearFloat : TransposerBase
    {
        private double _fract;

        public InterpolateLinearFloat()
        {
            ResetRegisters();
            SetRate(1.0);
        }

        public override int Latency => 0;

        public override void ResetRegisters()
        {
            _fract = 0;
        }

        protected override int TransposeMono(in Span<float> dest, in ReadOnlySpan<float> src, ref int srcSamples)
        {
            var psrc = src;

            int index;
            int srcSampleEnd = srcSamples - 1;
            int srcCount = 0;

            index = 0;
            while (srcCount < srcSampleEnd)
            {
                double @out;
                Debug.Assert(_fract < 1.0, "_fract < 1.0");

                @out = ((1.0 - _fract) * psrc[0]) + (_fract * psrc[1]);
                dest[index] = (float)@out;
                index++;

                // update position fraction
                _fract += Rate;

                // update whole positions
                int whole = (int)_fract;
                _fract -= whole;
                psrc = psrc.Slice(whole);
                srcCount += whole;
            }

            srcSamples = srcCount;
            return index;
        }

        protected override int TransposeStereo(in Span<float> dest, in ReadOnlySpan<float> src, ref int srcSamples)
        {
            var psrc = src;

            int index;
            int srcSampleEnd = srcSamples - 1;
            int srcCount = 0;

            index = 0;
            while (srcCount < srcSampleEnd)
            {
                double outLeft, outRight;
                Debug.Assert(_fract < 1.0, "_fract < 1.0");

                outLeft = ((1.0 - _fract) * psrc[0]) + (_fract * psrc[2]);
                outRight = ((1.0 - _fract) * psrc[1]) + (_fract * psrc[3]);
                dest[2 * index] = (float)outLeft;
                dest[(2 * index) + 1] = (float)outRight;
                index++;

                // update position fraction
                _fract += Rate;

                // update whole positions
                int whole = (int)_fract;
                _fract -= whole;
                psrc = psrc.Slice(2 * whole);
                srcCount += whole;
            }

            srcSamples = srcCount;
            return index;
        }

        protected override int TransposeMulti(in Span<float> dest, in ReadOnlySpan<float> src, ref int srcSamples)
        {
            var psrc = src;
            var pdest = dest;

            int index;
            int srcSampleEnd = srcSamples - 1;
            int srcCount = 0;

            index = 0;
            while (srcCount < srcSampleEnd)
            {
                float temp, vol1, fract_float;

                vol1 = (float)(1.0 - _fract);
                fract_float = (float)_fract;
                for (int c = 0; c < NumberOfChannels; c++)
                {
                    temp = (vol1 * psrc[c]) + (fract_float * psrc[c + NumberOfChannels]);
                    pdest[0] = temp;
                    pdest = pdest.Slice(1);
                }

                index++;

                _fract += Rate;

                int iWhole = (int)_fract;
                _fract -= iWhole;
                srcCount += iWhole;
                psrc = psrc.Slice(iWhole * NumberOfChannels);
            }

            srcSamples = srcCount;

            return index;
        }
    }
}
