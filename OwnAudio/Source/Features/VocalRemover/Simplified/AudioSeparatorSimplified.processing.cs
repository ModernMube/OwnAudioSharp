using Ownaudio;
using Ownaudio.Decoders;
using Ownaudio.Core;
using System.Numerics;

namespace OwnaudioNET.Features.Vocalremover
{
    public partial class SimpleAudioSeparationService
    {
        #region Private Methods - Audio Processing

        /// <summary>
        /// Process audio file using streaming approach to minimize memory usage
        /// </summary>
        private (float[,] vocals, float[,] instrumental) ProcessAudioStreaming(string inputFilePath)
        {
            using var decoder = AudioDecoderFactory.Create(
                inputFilePath,
                targetSampleRate: TargetSampleRate,
                targetChannels: 2
            );

            AudioStreamInfo info = decoder.StreamInfo;
            int totalFrames = (int)(info.Duration.TotalSeconds * TargetSampleRate);

            var vocals = new float[2, totalFrames];
            var instrumental = new float[2, totalFrames];

            int margin = _options.Margin;
            int chunkSize = _options.ChunkSizeSeconds * TargetSampleRate;

            if (margin == 0) throw new ArgumentException("Margin cannot be zero!");
            if (chunkSize != 0 && margin > chunkSize) margin = chunkSize;
            if (_options.ChunkSizeSeconds == 0 || totalFrames < chunkSize) chunkSize = totalFrames;

            using var context = new ProcessingContext(_modelParams);

            int processedFrames = 0;
            int chunkIndex = 0;
            int totalChunks = (int)Math.Ceiling((double)totalFrames / chunkSize);

            int framesPerBuffer = Math.Min(chunkSize, 8192);
            int bufferSizeInBytes = framesPerBuffer * 2 * sizeof(float);
            byte[] readBuffer = new byte[bufferSizeInBytes];

            var chunkAccumulator = new List<float>();

            while (processedFrames < totalFrames)
            {
                var result = decoder.ReadFrames(readBuffer);

                if (!result.IsSucceeded || result.FramesRead == 0)
                {
                    if (result.IsEOF) break;
                    continue;
                }

                int bytesRead = result.FramesRead * 2 * sizeof(float);
                var floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                    readBuffer.AsSpan(0, bytesRead)
                );

                chunkAccumulator.AddRange(floatSpan.ToArray());

                while (chunkAccumulator.Count >= chunkSize * 2)
                {
                    var chunkData = new float[2, chunkSize];
                    for (int i = 0; i < chunkSize; i++)
                    {
                        chunkData[0, i] = chunkAccumulator[i * 2];
                        chunkData[1, i] = chunkAccumulator[i * 2 + 1];
                    }

                    chunkAccumulator.RemoveRange(0, chunkSize * 2);

                    var separated = ProcessSingleChunkOptimized(chunkData, context);

                    for (int ch = 0; ch < 2; ch++)
                    {
                        for (int i = 0; i < chunkSize; i++)
                        {
                            int outIdx = processedFrames + i;
                            if (outIdx < totalFrames)
                            {
                                instrumental[ch, outIdx] = separated[ch, i];
                                vocals[ch, outIdx] = chunkData[ch, i] - separated[ch, i];
                            }
                        }
                    }

                    processedFrames += chunkSize;
                    chunkIndex++;

                    ReportProgress(new SimpleSeparationProgress
                    {
                        Status = $"Processing chunk {chunkIndex}/{totalChunks}",
                        ProcessedChunks = chunkIndex,
                        TotalChunks = totalChunks,
                        OverallProgress = 20 + ((double)chunkIndex / totalChunks * 70)
                    });
                }
            }

            // Process remaining samples
            if (chunkAccumulator.Count > 0)
            {
                int remainingFrames = chunkAccumulator.Count / 2;
                var chunkData = new float[2, remainingFrames];
                for (int i = 0; i < remainingFrames; i++)
                {
                    chunkData[0, i] = chunkAccumulator[i * 2];
                    chunkData[1, i] = chunkAccumulator[i * 2 + 1];
                }

                var separated = ProcessSingleChunkOptimized(chunkData, context);

                for (int ch = 0; ch < 2; ch++)
                {
                    for (int i = 0; i < remainingFrames; i++)
                    {
                        int outIdx = processedFrames + i;
                        if (outIdx < totalFrames)
                        {
                            instrumental[ch, outIdx] = separated[ch, i];
                            vocals[ch, outIdx] = chunkData[ch, i] - separated[ch, i];
                        }
                    }
                }
            }

            return (vocals, instrumental);
        }

        #endregion

        #region Private Methods - Single Chunk Processing

        /// <summary>
        /// Optimized single chunk processing with buffer reuse
        /// </summary>
        private float[,] ProcessSingleChunkOptimized(float[,] mixChunk, ProcessingContext context)
        {
            int nSample = mixChunk.GetLength(1);
            int trim = _modelParams.NFft / 2;
            int genSize = _modelParams.ChunkSize - 2 * trim;

            if (genSize <= 0)
                throw new ArgumentException($"Invalid genSize: {genSize}. Check FFT parameters.");

            int pad = genSize - (nSample % genSize);
            if (nSample % genSize == 0) pad = 0;

            var mixPadded = new float[2, trim + nSample + pad + trim];
            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < nSample; i++)
                {
                    mixPadded[ch, trim + i] = mixChunk[ch, i];
                }
            }

            int frameCount = (nSample + pad) / genSize;
            var mixWaves = new float[frameCount, 2, _modelParams.ChunkSize];

            for (int i = 0; i < frameCount; i++)
            {
                int offset = i * genSize;
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int j = 0; j < _modelParams.ChunkSize; j++)
                    {
                        mixWaves[i, ch, j] = mixPadded[ch, offset + j];
                    }
                }
            }

            var stftTensor = ComputeStftOptimized(mixWaves, context);
            var outputTensor = RunModelInference(stftTensor);
            var resultWaves = ComputeIstftOptimized(outputTensor, context);

            return ExtractSignal(resultWaves, nSample, trim, genSize);
        }

        #endregion

        #region Private Methods - Signal Processing

        private float[,] ExtractSignal(float[,,] waves, int nSample, int trim, int genSize)
        {
            int frameCount = waves.GetLength(0);
            var signal = new float[2, nSample];

            for (int i = 0; i < frameCount; i++)
            {
                int destOffset = i * genSize;
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int j = 0; j < genSize && destOffset + j < nSample; j++)
                    {
                        int sourceIndex = trim + j;
                        if (sourceIndex < _modelParams.ChunkSize - trim)
                        {
                            signal[ch, destOffset + j] = waves[i, ch, sourceIndex];
                        }
                    }
                }
            }

            return signal;
        }

        private float[,] ApplyMargin(float[,] signal, long chunkKey, List<long> allKeys, int margin)
        {
            int nSample = signal.GetLength(1);
            int start = chunkKey == 0 ? 0 : margin;
            int end = chunkKey == allKeys.Last() ? nSample : nSample - margin;
            if (margin == 0) end = nSample;

            var result = new float[2, end - start];
            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < end - start; i++)
                {
                    result[ch, i] = signal[ch, start + i];
                }
            }

            return result;
        }

        private float[,] ConcatenateChunks(List<float[,]> chunks)
        {
            int totalLength = chunks.Sum(c => c.GetLength(1));
            var result = new float[2, totalLength];
            int currentPos = 0;

            foreach (var chunk in chunks)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int i = 0; i < chunk.GetLength(1); i++)
                    {
                        result[ch, currentPos + i] = chunk[ch, i];
                    }
                }
                currentPos += chunk.GetLength(1);
            }

            return result;
        }

        #endregion

        #region Private - Processing Context

        /// <summary>
        /// Processing context to reuse buffers and reduce GC pressure
        /// </summary>
        private class ProcessingContext : IDisposable
        {
            public float[] PaddedSignalL { get; }
            public float[] PaddedSignalR { get; }
            public System.Numerics.Complex[] FftFrame { get; }
            public double[] ReconstructedL { get; }
            public double[] ReconstructedR { get; }
            public double[] WindowSumL { get; }
            public double[] WindowSumR { get; }

            public ProcessingContext(ModelParameters modelParams)
            {
                int padSize = modelParams.NFft / 2;
                int maxChunkSize = modelParams.ChunkSize + 2 * padSize;

                PaddedSignalL = new float[maxChunkSize];
                PaddedSignalR = new float[maxChunkSize];
                FftFrame = new System.Numerics.Complex[modelParams.NFft];
                ReconstructedL = new double[maxChunkSize];
                ReconstructedR = new double[maxChunkSize];
                WindowSumL = new double[maxChunkSize];
                WindowSumR = new double[maxChunkSize];
            }

            public void Clear()
            {
                Array.Clear(PaddedSignalL, 0, PaddedSignalL.Length);
                Array.Clear(PaddedSignalR, 0, PaddedSignalR.Length);
                Array.Clear(ReconstructedL, 0, ReconstructedL.Length);
                Array.Clear(ReconstructedR, 0, ReconstructedR.Length);
                Array.Clear(WindowSumL, 0, WindowSumL.Length);
                Array.Clear(WindowSumR, 0, WindowSumR.Length);
            }

            public void Dispose()
            {
                // Nothing to dispose, arrays are managed
            }
        }

        #endregion
    }
}
