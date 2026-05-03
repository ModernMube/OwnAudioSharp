namespace SoundTouch
{
    using System;
    using System.Diagnostics;

    /// <summary>
    /// Abstract base class for transposer implementations (linear, advanced vs integer, float etc).
    /// </summary>
    internal abstract partial class TransposerBase
    {
        private static Algorithm _algorithm = Algorithm.Cubic;

        protected TransposerBase()
        {
            NumberOfChannels = 0;
            Rate = 1.0f;
        }

        public double Rate { get; private set; }

        public int NumberOfChannels { get; private set; }

        public virtual int Latency => 0;

        // static factory function
        public static TransposerBase CreateInstance()
        {
            switch (_algorithm)
            {
                case Algorithm.Linear:
                    return new InterpolateLinearFloat();

                case Algorithm.Cubic:
                    return new InterpolateCubic();

                case Algorithm.Shannon:
                    return new InterpolateShannon();

                default:
                    throw new NotSupportedException();
            }
        }

        // static function to set interpolation algorithm
        public static void SetAlgorithm(Algorithm a) => _algorithm = a;

        public virtual int Transpose(in FifoSampleBuffer dest, in FifoSampleBuffer src)
        {
            int numSrcSamples = src.AvailableSamples;
            int sizeDemand = (int)(numSrcSamples / Rate) + 8;
            int numOutput;
            var psrc = src.PtrBegin();
            var pdest = dest.PtrEnd(sizeDemand);

#if !USE_MULTICH_ALWAYS
            if (NumberOfChannels == 1)
            {
                numOutput = TransposeMono(pdest, psrc, ref numSrcSamples);
            }
            else if (NumberOfChannels == 2)
            {
                numOutput = TransposeStereo(pdest, psrc, ref numSrcSamples);
            }
            else
#endif // USE_MULTICH_ALWAYS
            {
                Debug.Assert(NumberOfChannels > 0, "Multiple channels");
                numOutput = TransposeMulti(pdest, psrc, ref numSrcSamples);
            }

            dest.PutSamples(numOutput);
            src.ReceiveSamples(numSrcSamples);
            return numOutput;
        }

        public virtual void SetRate(double newRate)
        {
            Rate = newRate;
        }

        public virtual void SetChannels(int channels)
        {
            NumberOfChannels = channels;
            ResetRegisters();
        }

        public abstract void ResetRegisters();

        protected abstract int TransposeMono(in Span<float> dest, in ReadOnlySpan<float> src, ref int srcSamples);

        protected abstract int TransposeStereo(in Span<float> dest, in ReadOnlySpan<float> src, ref int srcSamples);

        protected abstract int TransposeMulti(in Span<float> dest, in ReadOnlySpan<float> src, ref int srcSamples);
    }
}
