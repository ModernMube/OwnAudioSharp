using MathNet.Numerics.IntegralTransforms;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Numerics;

namespace OwnaudioNET.Features.Vocalremover
{
    public partial class MultiModelAudioSeparator
    {
        #region Private Methods - STFT/ISTFT

        /// <summary>
        /// Compute STFT with pre-calculated Hanning window
        /// </summary>
        private DenseTensor<float> ComputeStftOptimized(
            float[,,] mixWaves,
            ModelParameters modelParams,
            ProcessingContext context)
        {
            int batchSize = mixWaves.GetLength(0);
            var tensor = new DenseTensor<float>(new[] { batchSize, 4, modelParams.DimF, modelParams.DimT });
            var hanningWindow = _hanningWindows[modelParams.NFft];

            for (int b = 0; b < batchSize; b++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int padSize = modelParams.NFft / 2;
                    var paddedSignal = ch == 0 ? context.PaddedSignalL : context.PaddedSignalR;

                    Array.Clear(paddedSignal, 0, paddedSignal.Length);

                    // Reflection padding
                    for (int i = 0; i < padSize; i++)
                    {
                        int srcIdx = Math.Min(padSize - 1 - i, modelParams.ChunkSize - 1);
                        paddedSignal[i] = mixWaves[b, ch, srcIdx];
                    }

                    for (int i = 0; i < modelParams.ChunkSize; i++)
                    {
                        paddedSignal[padSize + i] = mixWaves[b, ch, i];
                    }

                    for (int i = 0; i < padSize; i++)
                    {
                        int srcIdx = Math.Max(0, modelParams.ChunkSize - 1 - i);
                        paddedSignal[padSize + modelParams.ChunkSize + i] = mixWaves[b, ch, srcIdx];
                    }

                    // STFT computation
                    for (int t = 0; t < modelParams.DimT; t++)
                    {
                        int frameStart = t * modelParams.Hop;

                        for (int i = 0; i < modelParams.NFft; i++)
                        {
                            if (frameStart + i < paddedSignal.Length)
                            {
                                context.FftFrame[i] = new Complex(paddedSignal[frameStart + i] * hanningWindow[i], 0);
                            }
                            else
                            {
                                context.FftFrame[i] = Complex.Zero;
                            }
                        }

                        Fourier.Forward(context.FftFrame, FourierOptions.NoScaling);

                        for (int f = 0; f < Math.Min(modelParams.DimF, modelParams.NBins); f++)
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
        /// Compute ISTFT with pre-calculated Hanning window
        /// </summary>
        private float[,,] ComputeIstftOptimized(
            Tensor<float> spectrum,
            ModelParameters modelParams,
            ProcessingContext context)
        {
            int batchSize = spectrum.Dimensions[0];
            var result = new float[batchSize, 2, modelParams.ChunkSize];
            var hanningWindow = _hanningWindows[modelParams.NFft];

            for (int b = 0; b < batchSize; b++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int padSize = modelParams.NFft / 2;
                    var reconstructed = ch == 0 ? context.ReconstructedL : context.ReconstructedR;
                    var windowSum = ch == 0 ? context.WindowSumL : context.WindowSumR;

                    Array.Clear(reconstructed, 0, reconstructed.Length);
                    Array.Clear(windowSum, 0, windowSum.Length);

                    int realIdx = ch * 2;
                    int imagIdx = ch * 2 + 1;

                    for (int t = 0; t < modelParams.DimT; t++)
                    {
                        for (int f = 0; f < modelParams.NBins && f < modelParams.NFft; f++)
                        {
                            if (f < modelParams.DimF && f < spectrum.Dimensions[2])
                            {
                                context.FftFrame[f] = new Complex(spectrum[b, realIdx, f, t], spectrum[b, imagIdx, f, t]);
                            }
                            else
                            {
                                context.FftFrame[f] = Complex.Zero;
                            }
                        }

                        // Hermitian symmetry
                        for (int f = 1; f < modelParams.NFft / 2; f++)
                        {
                            if (modelParams.NFft - f < context.FftFrame.Length)
                            {
                                context.FftFrame[modelParams.NFft - f] = Complex.Conjugate(context.FftFrame[f]);
                            }
                        }

                        Fourier.Inverse(context.FftFrame, FourierOptions.NoScaling);

                        for (int i = 0; i < modelParams.NFft; i++)
                        {
                            context.FftFrame[i] /= modelParams.NFft;
                        }

                        // Overlap-add
                        int frameStart = t * modelParams.Hop;
                        for (int i = 0; i < modelParams.NFft; i++)
                        {
                            int targetIdx = frameStart + i;
                            if (targetIdx >= 0 && targetIdx < reconstructed.Length)
                            {
                                float windowValue = hanningWindow[i];
                                reconstructed[targetIdx] += context.FftFrame[i].Real * windowValue;
                                windowSum[targetIdx] += windowValue * windowValue;
                            }
                        }
                    }

                    for (int i = 0; i < modelParams.ChunkSize; i++)
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
