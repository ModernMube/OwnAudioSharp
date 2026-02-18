using Ownaudio;
using Ownaudio.Decoders;
using Ownaudio.Core;
using System.Numerics;
using Logger;

namespace OwnaudioNET.Features.Vocalremover
{
    public partial class MultiModelAudioSeparator
    {
        #region Inner Classes

        /// <summary>
        /// Processing context to reuse buffers and reduce GC pressure
        /// </summary>
        private class ProcessingContext : IDisposable
        {
            public float[] PaddedSignalL { get; }
            public float[] PaddedSignalR { get; }
            public Complex[] FftFrame { get; }
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
                FftFrame = new Complex[modelParams.NFft];
                ReconstructedL = new double[maxChunkSize];
                ReconstructedR = new double[maxChunkSize];
                WindowSumL = new double[maxChunkSize];
                WindowSumR = new double[maxChunkSize];
            }

            public void Dispose()
            {
                // Arrays are managed, nothing to dispose
            }
        }

        #endregion

        #region Private Methods - Streaming Pipeline

        /// <summary>
        /// Process audio file using streaming pipeline with multi-model averaging.
        /// Each chunk is processed through ALL models in parallel, then vocals and instrumentals are averaged separately.
        /// </summary>
        private float[,] ProcessAudioStreamingPipeline(
            string inputFilePath,
            string filename,
            out Dictionary<string, string> intermediatePaths)
        {
            intermediatePaths = new Dictionary<string, string>();

            using var decoder = AudioDecoderFactory.Create(
                inputFilePath,
                targetSampleRate: TargetSampleRate,
                targetChannels: 2
            );

            AudioStreamInfo info = decoder.StreamInfo;
            int totalFrames = (int)(info.Duration.TotalSeconds * TargetSampleRate);

            var vocalsOutput = new float[2, totalFrames];
            var finalOutput = new float[2, totalFrames];

            int margin = _options.Margin;
            int chunkSize = _options.ChunkSizeSeconds * TargetSampleRate;

            if (margin == 0) throw new ArgumentException("Margin cannot be zero!");
            if (chunkSize != 0 && margin > chunkSize) margin = chunkSize;
            if (_options.ChunkSizeSeconds == 0 || totalFrames < chunkSize) chunkSize = totalFrames;

            var contexts = _modelSessions.Select(ms => new ProcessingContext(ms.Parameters)).ToList();

            Dictionary<int, List<float[,]>> intermediateVocalsChunks = new();
            Dictionary<int, List<float[,]>> intermediateInstrumentalChunks = new();
            if (_options.SaveAllIntermediateResults)
            {
                for (int i = 0; i < _modelSessions.Count; i++)
                {
                    intermediateVocalsChunks[i] = new List<float[,]>();
                    intermediateInstrumentalChunks[i] = new List<float[,]>();
                }
            }

            int processedFrames = 0;
            int chunkIndex = 0;
            int totalChunks = (int)Math.Ceiling((double)totalFrames / chunkSize);

            int framesPerBuffer = Math.Min(chunkSize, 8192);
            int bufferSizeInBytes = framesPerBuffer * 2 * sizeof(float);
            byte[] readBuffer = new byte[bufferSizeInBytes];

            var chunkAccumulator = new List<float>();

            Log.Info($"Multi-model averaging pipeline: {totalChunks} chunks, {_modelSessions.Count} models");
            Log.Info($"Models will be processed in parallel and results will be averaged");

            while (processedFrames < totalFrames)
            {
                // Read audio data into accumulator
                while (chunkAccumulator.Count < chunkSize * 2 && processedFrames + chunkAccumulator.Count / 2 < totalFrames)
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
                }

                if (chunkAccumulator.Count == 0)
                    break;

                int currentChunkSize = Math.Min(chunkAccumulator.Count / 2, chunkSize);
                if (processedFrames + currentChunkSize > totalFrames)
                    currentChunkSize = totalFrames - processedFrames;

                var chunkData = new float[2, currentChunkSize];
                for (int i = 0; i < currentChunkSize; i++)
                {
                    chunkData[0, i] = chunkAccumulator[i * 2];
                    chunkData[1, i] = chunkAccumulator[i * 2 + 1];
                }

                if (currentChunkSize * 2 <= chunkAccumulator.Count)
                    chunkAccumulator.RemoveRange(0, currentChunkSize * 2);
                else
                    chunkAccumulator.Clear();

                var originalChunk = (float[,])chunkData.Clone();

                var vocalsAccumulator = new float[2, currentChunkSize];
                var instrumentalAccumulator = new float[2, currentChunkSize];

                // Process chunk through ALL models
                for (int modelIdx = 0; modelIdx < _modelSessions.Count; modelIdx++)
                {
                    var modelSession = _modelSessions[modelIdx];
                    var context = contexts[modelIdx];

                    var processedChunk = ProcessSingleChunk(originalChunk, modelSession, context);

                    float[,] vocals, instrumental;

                    if (modelSession.ResolvedOutputType == ModelOutputType.Vocals)
                    {
                        vocals = processedChunk;
                        instrumental = new float[2, currentChunkSize];

                        for (int ch = 0; ch < 2; ch++)
                            for (int i = 0; i < currentChunkSize; i++)
                                instrumental[ch, i] = originalChunk[ch, i] - processedChunk[ch, i];
                    }
                    else
                    {
                        instrumental = processedChunk;
                        vocals = new float[2, currentChunkSize];

                        for (int ch = 0; ch < 2; ch++)
                            for (int i = 0; i < currentChunkSize; i++)
                                vocals[ch, i] = originalChunk[ch, i] - processedChunk[ch, i];
                    }

                    for (int ch = 0; ch < 2; ch++)
                    {
                        for (int i = 0; i < currentChunkSize; i++)
                        {
                            vocalsAccumulator[ch, i] += vocals[ch, i];
                            instrumentalAccumulator[ch, i] += instrumental[ch, i];
                        }
                    }

                    if (_options.SaveAllIntermediateResults || modelSession.Info.SaveIntermediateOutput)
                    {
                        if (!intermediateVocalsChunks.ContainsKey(modelIdx))
                            intermediateVocalsChunks[modelIdx] = new List<float[,]>();
                        if (!intermediateInstrumentalChunks.ContainsKey(modelIdx))
                            intermediateInstrumentalChunks[modelIdx] = new List<float[,]>();

                        intermediateVocalsChunks[modelIdx].Add((float[,])vocals.Clone());
                        intermediateInstrumentalChunks[modelIdx].Add((float[,])instrumental.Clone());
                    }

                    double modelProgressBase = (double)modelIdx / _modelSessions.Count * 90;
                    double modelProgressRange = 90.0 / _modelSessions.Count;
                    double chunkProgress = (double)(chunkIndex + 1) / totalChunks * modelProgressRange;

                    ReportProgress(new MultiModelSeparationProgress
                    {
                        Status = $"{modelSession.Info.Name}: chunk {chunkIndex + 1}/{totalChunks}",
                        ProcessedChunks = chunkIndex + 1,
                        TotalChunks = totalChunks,
                        CurrentModelIndex = modelIdx + 1,
                        TotalModels = _modelSessions.Count,
                        CurrentModelName = modelSession.Info.Name,
                        OverallProgress = modelProgressBase + chunkProgress
                    });
                }

                // Average results from all models
                float modelCount = (float)_modelSessions.Count;
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int i = 0; i < currentChunkSize; i++)
                    {
                        int outIdx = processedFrames + i;
                        if (outIdx < totalFrames)
                        {
                            vocalsOutput[ch, outIdx] = vocalsAccumulator[ch, i] / modelCount;
                            finalOutput[ch, outIdx] = instrumentalAccumulator[ch, i] / modelCount;
                        }
                    }
                }

                processedFrames += currentChunkSize;
                chunkIndex++;
            }

            foreach (var context in contexts)
            {
                context.Dispose();
            }

            // Save intermediate results if requested
            if (_options.SaveAllIntermediateResults)
            {
                for (int modelIdx = 0; modelIdx < _modelSessions.Count; modelIdx++)
                {
                    var modelSession = _modelSessions[modelIdx];

                    if (intermediateVocalsChunks.ContainsKey(modelIdx))
                    {
                        var vocalsAudio = ConcatenateChunks(intermediateVocalsChunks[modelIdx]);
                        string vocalsPath = Path.Combine(
                            _options.OutputDirectory,
                            $"{filename}_model{modelIdx + 1}_{modelSession.Info.Name}_vocals.wav"
                        );
                        Directory.CreateDirectory(_options.OutputDirectory);
                        SaveAudio(vocalsPath, vocalsAudio, TargetSampleRate);
                        intermediatePaths[$"Model{modelIdx + 1}_{modelSession.Info.Name}_Vocals"] = vocalsPath;
                        Log.Info($"Saved intermediate vocals: {vocalsPath}");
                    }

                    if (intermediateInstrumentalChunks.ContainsKey(modelIdx))
                    {
                        var instrumentalAudio = ConcatenateChunks(intermediateInstrumentalChunks[modelIdx]);
                        string instrumentalPath = Path.Combine(
                            _options.OutputDirectory,
                            $"{filename}_model{modelIdx + 1}_{modelSession.Info.Name}_instrumental.wav"
                        );
                        Directory.CreateDirectory(_options.OutputDirectory);
                        SaveAudio(instrumentalPath, instrumentalAudio, TargetSampleRate);
                        intermediatePaths[$"Model{modelIdx + 1}_{modelSession.Info.Name}_Instrumental"] = instrumentalPath;
                        Log.Info($"Saved intermediate instrumental: {instrumentalPath}");
                    }
                }
            }

            // Save averaged vocals
            Directory.CreateDirectory(_options.OutputDirectory);
            string finalVocalsPath = Path.Combine(_options.OutputDirectory, $"{filename}_vocals.wav");
            SaveAudio(finalVocalsPath, vocalsOutput, TargetSampleRate);
            intermediatePaths["Vocals"] = finalVocalsPath;
            Log.Info($"Saved averaged vocals from {_modelSessions.Count} models: {finalVocalsPath}");

            return finalOutput;
        }

        #endregion

        #region Private Methods - Model Processing

        /// <summary>
        /// Process a single audio chunk through a model
        /// </summary>
        private float[,] ProcessSingleChunk(float[,] mixChunk, ModelSession modelSession, ProcessingContext context)
        {
            int nSample = mixChunk.GetLength(1);
            int trim = modelSession.Parameters.NFft / 2;
            int genSize = modelSession.Parameters.ChunkSize - 2 * trim;

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
            var mixWaves = new float[frameCount, 2, modelSession.Parameters.ChunkSize];

            for (int i = 0; i < frameCount; i++)
            {
                int offset = i * genSize;
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int j = 0; j < modelSession.Parameters.ChunkSize; j++)
                    {
                        mixWaves[i, ch, j] = mixPadded[ch, offset + j];
                    }
                }
            }

            var stftTensor = ComputeStftOptimized(mixWaves, modelSession.Parameters, context);
            var outputTensor = RunModelInference(stftTensor, modelSession);
            var resultWaves = ComputeIstftOptimized(outputTensor, modelSession.Parameters, context);

            return ExtractSignal(resultWaves, nSample, trim, genSize, modelSession.Parameters);
        }

        #endregion

        #region Private Methods - Signal Processing

        private float[,] ExtractSignal(float[,,] waves, int nSample, int trim, int genSize, ModelParameters modelParams)
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
                        if (sourceIndex < modelParams.ChunkSize - trim)
                        {
                            signal[ch, destOffset + j] = waves[i, ch, sourceIndex];
                        }
                    }
                }
            }

            return signal;
        }

        private float[,] ConcatenateChunks(List<float[,]> chunks)
        {
            if (chunks.Count == 0)
                return new float[2, 0];

            int totalLength = chunks.Sum(c => c.GetLength(1));
            var result = new float[2, totalLength];
            int currentPos = 0;

            foreach (var chunk in chunks)
            {
                int chunkLength = chunk.GetLength(1);
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int i = 0; i < chunkLength; i++)
                    {
                        result[ch, currentPos + i] = chunk[ch, i];
                    }
                }
                currentPos += chunkLength;
            }

            return result;
        }

        #endregion
    }
}
