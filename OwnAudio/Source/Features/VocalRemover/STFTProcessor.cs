using MathNet.Numerics.IntegralTransforms;
using System.Numerics;

namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>
    /// Shared STFT (Short-Time Fourier Transform) processor for audio separation models
    /// Provides optimized STFT computation with pre-calculated Hann windows
    /// </summary>
    public class STFTProcessor
    {
        private readonly int _nFft;
        private readonly int _hopLength;
        private readonly float[] _hannWindow;

        /// <summary>
        /// Initialize STFT processor with specified parameters
        /// </summary>
        /// <param name="nFft">FFT window size</param>
        /// <param name="hopLength">Hop length (stride) between frames</param>
        public STFTProcessor(int nFft, int hopLength)
        {
            _nFft = nFft;
            _hopLength = hopLength;
            _hannWindow = GenerateHannWindow(nFft);
        }

        /// <summary>
        /// Generate Hann window for STFT
        /// </summary>
        private static float[] GenerateHannWindow(int windowSize)
        {
            var window = new float[windowSize];
            for (int i = 0; i < windowSize; i++)
            {
                window[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / windowSize));
            }
            return window;
        }

        /// <summary>
        /// Compute STFT spectrogram for HTDemucs model
        /// Uses PyTorch HTDemucs-style padding: hop_length/2 * 3 (NOT n_fft/2)
        /// Reference: htdemucs.py _spec() method lines 420-440
        /// </summary>
        /// <param name="audioChunk">Audio chunk [channels, samples]</param>
        /// <returns>Spectrogram tensor [batch, channels, freq_bins, time_frames, complex_components]</returns>
        public float[,,,,] ComputeSpectrogram(float[,] audioChunk)
        {
            int freq_bins = _nFft / 2; // Only positive frequencies
            int channels = audioChunk.GetLength(0);
            int samples = audioChunk.GetLength(1);
            
            // HTDemucs uses special padding: pad = hop_length // 2 * 3
            // Reference: Python line 434: pad = hl // 2 * 3
            int padSize = _hopLength / 2 * 3;  // = 1024 / 2 * 3 = 1536
            
            // Python line 433: le = int(math.ceil(x.shape[-1] / hl))
            // For 441000 samples: ceil(441000 / 1024) = ceil(430.664) = 431
            int expectedFrames = (int)Math.Ceiling((double)samples / _hopLength);
            
            // Python line 435: x = pad1d(x, (pad, pad + le * hl - x.shape[-1]), mode="reflect")
            // Right padding size: pad + le * hl - samples
            int rightPadSize = padSize + expectedFrames * _hopLength - samples;
            int paddedLength = samples + padSize + rightPadSize;
            
            // After STFT with this padding, we get expectedFrames + 4 frames
            // Python line 438: assert z.shape[-1] == le + 4
            int n_frames_with_padding = expectedFrames + 4;
            
            var spectrogram_raw = new float[1, channels, freq_bins, n_frames_with_padding, 2];
            
            // Process each channel
            for (int ch = 0; ch < channels; ch++)
            {
                // Create padded signal with reflection padding
                var paddedSignal = new float[paddedLength];
                
                // Left reflection padding
                for (int i = 0; i < padSize; i++)
                {
                    int srcIdx = Math.Min(padSize - i, samples - 1);
                    paddedSignal[i] = audioChunk[ch, srcIdx];
                }
                
                // Copy original signal
                for (int i = 0; i < samples; i++)
                {
                    paddedSignal[padSize + i] = audioChunk[ch, i];
                }
                
                // Right reflection padding
                int rightPadStart = padSize + samples;
                for (int i = 0; i < rightPadSize; i++)
                {
                    int srcIdx = Math.Max(0, samples - 2 - i);
                    paddedSignal[rightPadStart + i] = audioChunk[ch, srcIdx];
                }
                
                // Process each frame
                for (int frame = 0; frame < n_frames_with_padding; frame++)
                {
                    int frameStart = frame * _hopLength;
                    
                    // Extract windowed frame
                    var frameData = new Complex[_nFft];
                    for (int i = 0; i < _nFft; i++)
                    {
                        int sampleIdx = frameStart + i;
                        if (sampleIdx < paddedLength)
                        {
                            frameData[i] = new Complex(paddedSignal[sampleIdx] * _hannWindow[i], 0);
                        }
                        else
                        {
                            frameData[i] = Complex.Zero;
                        }
                    }
                    
                    // Perform FFT (use NoScaling for consistency with ISTFT)
                    Fourier.Forward(frameData, FourierOptions.NoScaling);
                    
                    // Store first freq_bins frequency bins (positive frequencies only)
                    for (int f = 0; f < freq_bins; f++)
                    {
                        spectrogram_raw[0, ch, f, frame, 0] = (float)frameData[f].Real;
                        spectrogram_raw[0, ch, f, frame, 1] = (float)frameData[f].Imaginary;
                    }
                }
            }
            
            // Trim frames: z = z[..., 2: 2 + le]
            // Remove first 2 frames, keep exactly expectedFrames
            // Python line 439: z = z[..., 2: 2 + le]
            int trimStart = 2;
            
            var spectrogram = new float[1, channels, freq_bins, expectedFrames, 2];
            for (int ch = 0; ch < channels; ch++)
            {
                for (int f = 0; f < freq_bins; f++)
                {
                    for (int t = 0; t < expectedFrames; t++)
                    {
                        spectrogram[0, ch, f, t, 0] = spectrogram_raw[0, ch, f, trimStart + t, 0];
                        spectrogram[0, ch, f, t, 1] = spectrogram_raw[0, ch, f, trimStart + t, 1];
                    }
                }
            }
            
            return spectrogram;
        }

        /// <summary>
        /// Compute Inverse STFT to reconstruct waveform from spectrogram
        /// Uses HTDemucs-style padding and frame handling
        /// Reference: htdemucs.py _ispec() method lines 442-450
        /// </summary>
        /// <param name="spectrogram">Spectrogram [batch, channels, freq_bins, time_frames, complex_components]</param>
        /// <param name="targetLength">Target output length in samples</param>
        /// <returns>Reconstructed audio [channels, samples]</returns>
        public float[,] ComputeISTFT(float[,,,,] spectrogram, int targetLength)
        {
            int channels = spectrogram.GetLength(1);
            int freq_bins = spectrogram.GetLength(2);
            int n_frames = spectrogram.GetLength(3);
            
            // HTDemucs uses hop_length // 2 * 3 padding (NOT n_fft // 2)
            // Reference: Python line 446: pad = hl // 2 * 3
            int padSize = _hopLength / 2 * 3;  // = 1024 / 2 * 3 = 1536
            
            // Add time-domain padding: 2 frames on each side
            // Reference: Python lines 444-445
            int n_frames_padded = n_frames + 1 + 4;  // +1 for last frame, +4 for time padding (2 on each side)
            
            var spectrogram_padded = new float[1, channels, freq_bins, n_frames_padded, 2];
            
            // Copy spectrogram with time padding: 2 frames before, original, 1 zero frame after, 2 frames after
            for (int ch = 0; ch < channels; ch++)
            {
                for (int f = 0; f < freq_bins; f++)
                {
                    // Time padding: 2 frames at start
                    for (int t = 0; t < 2; t++)
                    {
                        spectrogram_padded[0, ch, f, t, 0] = 0;
                        spectrogram_padded[0, ch, f, t, 1] = 0;
                    }
                    
                    // Original frames
                    for (int t = 0; t < n_frames; t++)
                    {
                        spectrogram_padded[0, ch, f, 2 + t, 0] = spectrogram[0, ch, f, t, 0];
                        spectrogram_padded[0, ch, f, 2 + t, 1] = spectrogram[0, ch, f, t, 1];
                    }
                    
                    // One zero frame (Python: F.pad(z, (0, 0, 0, 1)))
                    spectrogram_padded[0, ch, f, 2 + n_frames, 0] = 0;
                    spectrogram_padded[0, ch, f, 2 + n_frames, 1] = 0;
                    
                    // Time padding: 2 frames at end
                    for (int t = 0; t < 2; t++)
                    {
                        spectrogram_padded[0, ch, f, 2 + n_frames + 1 + t, 0] = 0;
                        spectrogram_padded[0, ch, f, 2 + n_frames + 1 + t, 1] = 0;
                    }
                }
            }
            
            // Calculate padded output length
            int paddedLength = _hopLength * (int)Math.Ceiling((double)targetLength / _hopLength) + 2 * padSize;
            
            var result = new float[channels, targetLength];
            
            for (int ch = 0; ch < channels; ch++)
            {
                var reconstructed = new double[paddedLength];
                var windowSum = new double[paddedLength];
                var fftFrame = new Complex[_nFft];
                
                // Process each time frame (using padded spectrogram)
                for (int t = 0; t < n_frames_padded; t++)
                {
                    // Prepare FFT frame from spectrogram
                    for (int f = 0; f < freq_bins && f < _nFft; f++)
                    {
                        float real = spectrogram_padded[0, ch, f, t, 0];
                        float imag = spectrogram_padded[0, ch, f, t, 1];
                        fftFrame[f] = new Complex(real, imag);
                    }
                    
                    // Fill remaining bins with zeros
                    for (int f = freq_bins; f < _nFft; f++)
                    {
                        fftFrame[f] = Complex.Zero;
                    }
                    
                    // Apply Hermitian symmetry for real signal
                    for (int f = 1; f < _nFft / 2; f++)
                    {
                        fftFrame[_nFft - f] = Complex.Conjugate(fftFrame[f]);
                    }
                    
                    // Inverse FFT
                    Fourier.Inverse(fftFrame, FourierOptions.NoScaling);
                    
                    // Normalize by FFT size
                    for (int i = 0; i < _nFft; i++)
                    {
                        fftFrame[i] /= _nFft;
                    }
                    
                    // Overlap-add with Hann window
                    int frameStart = t * _hopLength;
                    for (int i = 0; i < _nFft; i++)
                    {
                        int targetIdx = frameStart + i;
                        if (targetIdx >= 0 && targetIdx < paddedLength)
                        {
                            float windowValue = _hannWindow[i];
                            reconstructed[targetIdx] += fftFrame[i].Real * windowValue;
                            windowSum[targetIdx] += windowValue * windowValue;
                        }
                    }
                }
                
                // Extract final result (remove padding) and normalize by window sum
                for (int i = 0; i < targetLength; i++)
                {
                    int srcIdx = i + padSize;
                    if (srcIdx >= 0 && srcIdx < paddedLength)
                    {
                        if (windowSum[srcIdx] > 1e-10)
                        {
                            result[ch, i] = (float)(reconstructed[srcIdx] / windowSum[srcIdx]);
                        }
                        else
                        {
                            result[ch, i] = (float)reconstructed[srcIdx];
                        }
                    }
                }
            }
            
            return result;
        }

        /// <summary>
        /// Flatten 5D array to 1D for tensor creation
        /// </summary>
        public static float[] Flatten5D(float[,,,,] array)
        {
            int d0 = array.GetLength(0);
            int d1 = array.GetLength(1);
            int d2 = array.GetLength(2);
            int d3 = array.GetLength(3);
            int d4 = array.GetLength(4);
            
            var result = new float[d0 * d1 * d2 * d3 * d4];
            int idx = 0;
            
            for (int i0 = 0; i0 < d0; i0++)
            for (int i1 = 0; i1 < d1; i1++)
            for (int i2 = 0; i2 < d2; i2++)
            for (int i3 = 0; i3 < d3; i3++)
            for (int i4 = 0; i4 < d4; i4++)
            {
                result[idx++] = array[i0, i1, i2, i3, i4];
            }
            
            return result;
        }

        /// <summary>
        /// Get the Hann window used by this processor
        /// </summary>
        public float[] HannWindow => _hannWindow;

        /// <summary>
        /// Get the FFT size
        /// </summary>
        public int NFft => _nFft;

        /// <summary>
        /// Get the hop length
        /// </summary>
        public int HopLength => _hopLength;
    }
}
