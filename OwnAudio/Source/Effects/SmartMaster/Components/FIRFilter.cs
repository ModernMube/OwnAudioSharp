using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// General FIR (Finite Impulse Response) filter class
    /// Linear phase, windowed-sinc design
    /// </summary>
    public class FIRFilter
    {
        /// <summary>
        /// Filter kernel (impulse response coefficients).
        /// </summary>
        private readonly float[] _kernel;
        
        /// <summary>
        /// Delay lines for each channel.
        /// </summary>
        private readonly float[][] _delayLines;
        
        /// <summary>
        /// Size of the filter kernel.
        /// </summary>
        private readonly int _kernelSize;
        
        /// <summary>
        /// Maximum number of audio channels supported.
        /// </summary>
        private readonly int _maxChannels;
        
        /// <summary>
        /// Current write positions in delay lines for each channel.
        /// </summary>
        private int[] _writePos;

        /// <summary>
        /// Initialize FIR filter with given kernel
        /// </summary>
        /// <param name="kernel">Filter kernel (impulse response)</param>
        /// <param name="maxChannels">Maximum number of channels (usually 2 for stereo)</param>
        public FIRFilter(float[] kernel, int maxChannels = 2)
        {
            _kernel = kernel;
            _kernelSize = kernel.Length;
            _maxChannels = maxChannels;
            
            // Initialize per-channel delay lines
            _delayLines = new float[maxChannels][];
            _writePos = new int[maxChannels];
            
            for (int ch = 0; ch < maxChannels; ch++)
            {
                _delayLines[ch] = new float[_kernelSize];
                _writePos[ch] = 0;
            }
        }

        /// <summary>
        /// Generate Bandpass filter kernel using windowed-sinc method
        /// </summary>
        /// <param name="sampleRate">Sample rate (Hz)</param>
        /// <param name="lowFreq">Low cutoff frequency (Hz)</param>
        /// <param name="highFreq">High cutoff frequency (Hz)</param>
        /// <param name="kernelSize">Kernel size (odd number recommended)</param>
        /// <param name="kaiserBeta">Kaiser window beta parameter (5.0 recommended)</param>
        /// <returns>FIR kernel</returns>
        public static float[] CreateBandpassKernel(float sampleRate, float lowFreq, float highFreq, int kernelSize, float kaiserBeta = 5.0f)
        {
            if (kernelSize % 2 == 0)
                kernelSize++; // Ensure odd kernel size (symmetry)

            float[] kernel = new float[kernelSize];
            int center = kernelSize / 2;
            
            // Normalized frequencies
            float wl = 2.0f * MathF.PI * lowFreq / sampleRate;
            float wh = 2.0f * MathF.PI * highFreq / sampleRate;

            // Windowed-sinc bandpass kernel generation
            for (int i = 0; i < kernelSize; i++)
            {
                int n = i - center;
                
                float sincValue;
                if (n == 0)
                {
                    // Special case: sinc(0) = 1
                    sincValue = (wh - wl) / MathF.PI;
                }
                else
                {
                    // Bandpass = highpass - lowpass
                    float highSinc = MathF.Sin(wh * n) / (MathF.PI * n);
                    float lowSinc = MathF.Sin(wl * n) / (MathF.PI * n);
                    sincValue = highSinc - lowSinc;
                }
                
                // Apply Kaiser window
                float window = KaiserWindow(i, kernelSize, kaiserBeta);
                kernel[i] = sincValue * window;
            }

            // Normalization (so gain is ~1.0 in passband)
            float sum = 0.0f;
            for (int i = 0; i < kernelSize; i++)
                sum += kernel[i];
            
            if (MathF.Abs(sum) > 1e-6f)
            {
                for (int i = 0; i < kernelSize; i++)
                    kernel[i] /= sum;
            }

            return kernel;
        }

        /// <summary>
        /// Calculate Kaiser window
        /// </summary>
        private static float KaiserWindow(int n, int size, float beta)
        {
            float alpha = (size - 1) / 2.0f;
            float x = (n - alpha) / alpha;
            
            // I0(beta * sqrt(1 - x^2)) / I0(beta)
            float arg = beta * MathF.Sqrt(1.0f - x * x);
            return BesselI0(arg) / BesselI0(beta);
        }

        /// <summary>
        /// Modified Bessel function I0 (zeroth order)
        /// Taylor series approximation
        /// </summary>
        private static float BesselI0(float x)
        {
            float sum = 1.0f;
            float term = 1.0f;
            float xSquared = x * x / 4.0f;

            for (int k = 1; k <= 20; k++)
            {
                term *= xSquared / (k * k);
                sum += term;
                
                if (term < 1e-8f * sum)
                    break;
            }

            return sum;
        }

        /// <summary>
        /// FIR filtering (convolution)
        /// </summary>
        /// <param name="buffer">Audio buffer</param>
        /// <param name="frameCount">Number of frames</param>
        /// <param name="channels">Number of channels</param>
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
                    float input = buffer[idx];

                    // Store input in delay line
                    delayLine[writePos] = input;

                    // Convolution (FIR filtering)
                    float output = 0.0f;
                    int readPos = writePos;

                    for (int k = 0; k < _kernelSize; k++)
                    {
                        output += _kernel[k] * delayLine[readPos];
                        readPos = (readPos == 0) ? (_kernelSize - 1) : (readPos - 1);
                    }

                    buffer[idx] = output;

                    // Update write position (circular buffer)
                    writePos = (writePos + 1) % _kernelSize;
                }

                _writePos[ch] = writePos;
            }
        }

        /// <summary>
        /// Clear filter state
        /// </summary>
        public void Reset()
        {
            for (int ch = 0; ch < _maxChannels; ch++)
            {
                Array.Clear(_delayLines[ch], 0, _kernelSize);
                _writePos[ch] = 0;
            }
        }
    }
}
