using MathNet.Numerics.IntegralTransforms;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Numerics;

namespace OwnaudioNET.Features.Vocalremover
{
    public partial class SimpleAudioSeparationService
    {
        #region Private Methods - STFT/ISTFT Processing

        /// <summary>
        /// Optimized STFT computation with pre-calculated Hanning window
        /// </summary>
        private DenseTensor<float> ComputeStftOptimized(float[,,] mixWaves, ProcessingContext context)
        {
            int batchSize = mixWaves.GetLength(0);
            var tensor = new DenseTensor<float>(new[] { batchSize, 4, _modelParams.DimF, _modelParams.DimT });

            for (int b = 0; b < batchSize; b++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int padSize = _modelParams.NFft / 2;
                    var paddedSignal = ch == 0 ? context.PaddedSignalL : context.PaddedSignalR;

                    Array.Clear(paddedSignal, 0, paddedSignal.Length);

                    // Reflection padding
                    for (int i = 0; i < padSize; i++)
                    {
                        int srcIdx = Math.Min(padSize - 1 - i, _modelParams.ChunkSize - 1);
                        paddedSignal[i] = mixWaves[b, ch, srcIdx];
                    }

                    for (int i = 0; i < _modelParams.ChunkSize; i++)
                    {
                        paddedSignal[padSize + i] = mixWaves[b, ch, i];
                    }

                    for (int i = 0; i < padSize; i++)
                    {
                        int srcIdx = Math.Max(0, _modelParams.ChunkSize - 1 - i);
                        paddedSignal[padSize + _modelParams.ChunkSize + i] = mixWaves[b, ch, srcIdx];
                    }

                    // STFT computation with pre-calculated window
                    for (int t = 0; t < _modelParams.DimT; t++)
                    {
                        int frameStart = t * _modelParams.Hop;

                        for (int i = 0; i < _modelParams.NFft; i++)
                        {
                            if (frameStart + i < paddedSignal.Length)
                            {
                                context.FftFrame[i] = new Complex(paddedSignal[frameStart + i] * _hanningWindow![i], 0);
                            }
                            else
                            {
                                context.FftFrame[i] = Complex.Zero;
                            }
                        }

                        Fourier.Forward(context.FftFrame, FourierOptions.NoScaling);

                        for (int f = 0; f < Math.Min(_modelParams.DimF, _modelParams.NBins); f++)
                        {
                            tensor[b, ch * 2, f, t] = (float)context.FftFrame[f].Real;
                            tensor[b, ch * 2 + 1, f, t] = (float)context.FftFrame[f].Imaginary;
                        }
                    }
                }
            }
            return tensor;
        }

        /// <summary>
        /// Optimized ISTFT computation with pre-calculated Hanning window and buffer reuse
        /// </summary>
        private float[,,] ComputeIstftOptimized(Tensor<float> spectrum, ProcessingContext context)
        {
            int batchSize = spectrum.Dimensions[0];
            var result = new float[batchSize, 2, _modelParams.ChunkSize];

            for (int b = 0; b < batchSize; b++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int padSize = _modelParams.NFft / 2;
                    var reconstructed = ch == 0 ? context.ReconstructedL : context.ReconstructedR;
                    var windowSum = ch == 0 ? context.WindowSumL : context.WindowSumR;

                    Array.Clear(reconstructed, 0, reconstructed.Length);
                    Array.Clear(windowSum, 0, windowSum.Length);

                    int realIdx = ch * 2;
                    int imagIdx = ch * 2 + 1;

                    for (int t = 0; t < _modelParams.DimT; t++)
                    {
                        for (int f = 0; f < _modelParams.NBins && f < _modelParams.NFft; f++)
                        {
                            if (f < _modelParams.DimF && f < spectrum.Dimensions[2])
                            {
                                context.FftFrame[f] = new Complex(spectrum[b, realIdx, f, t], spectrum[b, imagIdx, f, t]);
                            }
                            else
                            {
                                context.FftFrame[f] = Complex.Zero;
                            }
                        }

                        // Hermitian symmetry
                        for (int f = 1; f < _modelParams.NFft / 2; f++)
                        {
                            if (_modelParams.NFft - f < context.FftFrame.Length)
                            {
                                context.FftFrame[_modelParams.NFft - f] = Complex.Conjugate(context.FftFrame[f]);
                            }
                        }

                        Fourier.Inverse(context.FftFrame, FourierOptions.NoScaling);

                        for (int i = 0; i < _modelParams.NFft; i++)
                        {
                            context.FftFrame[i] /= _modelParams.NFft;
                        }

                        // Overlap-add with pre-calculated window
                        int frameStart = t * _modelParams.Hop;
                        for (int i = 0; i < _modelParams.NFft; i++)
                        {
                            int targetIdx = frameStart + i;
                            if (targetIdx >= 0 && targetIdx < reconstructed.Length)
                            {
                                float windowValue = _hanningWindow![i];
                                reconstructed[targetIdx] += context.FftFrame[i].Real * windowValue;
                                windowSum[targetIdx] += windowValue * windowValue;
                            }
                        }
                    }

                    for (int i = 0; i < _modelParams.ChunkSize; i++)
                    {
                        int srcIdx = i + padSize;
                        if (srcIdx >= 0 && srcIdx < reconstructed.Length)
                        {
                            if (windowSum[srcIdx] > 1e-10)
                            {
                                result[b, ch, i] = (float)(reconstructed[srcIdx] / windowSum[srcIdx]);
                            }
                            else
                            {
                                result[b, ch, i] = (float)reconstructed[srcIdx];
                            }
                        }
                    }
                }
            }
            return result;
        }

        #endregion
    }
}
