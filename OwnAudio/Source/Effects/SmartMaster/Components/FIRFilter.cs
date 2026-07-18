using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Plain windowed-sinc FIR filter, linear phase.
    /// </summary>
    public class FIRFilter
    {
        private readonly float[] _kernel;
        private readonly float[][] _delayLines;
        private readonly int _kernelSize;
        private readonly int _maxChannels;

        /// <summary>
        /// Ring buffer write cursor per channel.
        /// </summary>
        private readonly int[] _writePos;

        /// <summary>
        /// Wraps an already generated impulse response, maxChannels is
        /// how many independent delay lines we keep (2 for stereo).
        /// </summary>
        public FIRFilter(float[] kernel, int maxChannels = 2)
        {
            _kernel = kernel;
            _kernelSize = kernel.Length;
            _maxChannels = maxChannels;

            _delayLines = new float[maxChannels][];
            _writePos = new int[maxChannels];

            for (int ch = 0; ch < maxChannels; ch++)
                _delayLines[ch] = new float[_kernelSize];
        }

        /// <summary>
        /// Windowed-sinc bandpass kernel. Freqs in Hz, kernelSize is bumped to
        /// odd for symmetry, kaiserBeta shapes the window (5.0 is a sane default).
        /// </summary>
        /// <returns>FIR kernel</returns>
        public static float[] CreateBandpassKernel(float sampleRate, float lowFreq, float highFreq, int kernelSize, float kaiserBeta = 5.0f)
        {
            if (kernelSize % 2 == 0) kernelSize++;

            float[] kernel = new float[kernelSize];
            int center = kernelSize / 2;

            float wl = 2.0f * MathF.PI * lowFreq / sampleRate;
            float wh = 2.0f * MathF.PI * highFreq / sampleRate;

            for (int i = 0; i < kernelSize; i++)
            {
                int n = i - center;
                float sinc = n == 0
                    ? (wh - wl) / MathF.PI
                    : (MathF.Sin(wh * n) - MathF.Sin(wl * n)) / (MathF.PI * n);

                kernel[i] = sinc * _kaiser(i, kernelSize, kaiserBeta);
            }

            //A short kernel this low can't resolve the band, so its raw
            //coefficient sum leaks DC. Subtracting the mean forces zero DC gain
            //(still symmetric, so still linear phase), then we normalise at
            //the pass-band centre.
            float mean = 0.0f;
            for (int i = 0; i < kernelSize; i++)
                mean += kernel[i];
            mean /= kernelSize;
            for (int i = 0; i < kernelSize; i++)
                kernel[i] -= mean;

            float wc = MathF.PI * (lowFreq + highFreq) / sampleRate;
            float re = 0.0f, im = 0.0f;
            for (int i = 0; i < kernelSize; i++)
            {
                float nf = i - center;
                re += kernel[i] * MathF.Cos(wc * nf);
                im += kernel[i] * MathF.Sin(wc * nf);
            }

            float gain = MathF.Sqrt(re * re + im * im);
            if (gain > 1e-6f)
            {
                for (int i = 0; i < kernelSize; i++)
                    kernel[i] /= gain;
            }

            return kernel;
        }

        private static float _kaiser(int n, int size, float beta)
        {
            float alpha = (size - 1) / 2.0f;
            float x = (n - alpha) / alpha;
            return _besselI0(beta * MathF.Sqrt(1.0f - x * x)) / _besselI0(beta);
        }

        /// <summary>
        /// Modified Bessel I0, Taylor series until the terms stop mattering.
        /// </summary>
        private static float _besselI0(float x)
        {
            float sum = 1.0f;
            float term = 1.0f;
            float xSq = x * x / 4.0f;

            for (int k = 1; k <= 20; k++)
            {
                term *= xSq / (k * k);
                sum += term;
                if (term < 1e-8f * sum) break;
            }

            return sum;
        }

        /// <summary>
        /// Convolves an interleaved buffer in place.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount, int channels)
        {
            int actualChannels = Math.Min(channels, _maxChannels);

            for (int ch = 0; ch < actualChannels; ch++)
            {
                float[] delayLine = _delayLines[ch];
                int writePos = _writePos[ch];

                for (int frame = 0; frame < frameCount; frame++)
                {
                    int idx = frame * channels + ch;
                    delayLine[writePos] = buffer[idx];

                    float output = 0.0f;
                    int readPos = writePos;

                    for (int k = 0; k < _kernelSize; k++)
                    {
                        output += _kernel[k] * delayLine[readPos];
                        readPos = (readPos == 0) ? (_kernelSize - 1) : (readPos - 1);
                    }

                    buffer[idx] = output;

                    writePos++;
                    if (writePos >= _kernelSize) writePos = 0;
                }

                _writePos[ch] = writePos;
            }
        }

        /// <summary>
        /// Drops everything sitting in the delay lines.
        /// </summary>
        public void Reset()
        {
            for (int ch = 0; ch < _maxChannels; ch++)
            {
                Array.Clear(_delayLines[ch]);
                _writePos[ch] = 0;
            }
        }
    }
}
