using Ownaudio;
using Ownaudio.Decoders;
using Ownaudio.Core;
using Logger;
using BufferPool = OwnaudioNET.BufferManagement.AudioBufferPool;

namespace OwnaudioNET.Features.Vocalremover
{
    public partial class HTDemucsAudioSeparator
    {
        #region Inner Classes

        /// <summary>
        /// Processing context for buffer reuse and GC pressure reduction
        /// </summary>
        private class HTDemucsProcessingContext : IDisposable
        {
            private readonly BufferPool _readBufferPool;
            private readonly BufferPool _intermediateBufferPool;

            public float[]? RentedReadBuffer { get; private set; }
            public float[]? RentedIntermediateBuffer { get; private set; }

            public HTDemucsProcessingContext(int chunkSize, int overlap)
            {
                int maxChunkSize = chunkSize + 2 * overlap;
                int bufferSize = maxChunkSize * 2; // stereo

                _readBufferPool = new BufferPool(bufferSize, initialPoolSize: 1, maxPoolSize: 2);
                _intermediateBufferPool = new BufferPool(bufferSize, initialPoolSize: 1, maxPoolSize: 2);
            }

            public float[] GetReadBuffer()
            {
                if (RentedReadBuffer == null)
                    RentedReadBuffer = _readBufferPool.Rent();
                return RentedReadBuffer;
            }

            public float[] GetIntermediateBuffer()
            {
                if (RentedIntermediateBuffer == null)
                    RentedIntermediateBuffer = _intermediateBufferPool.Rent();
                return RentedIntermediateBuffer;
            }

            public void Dispose()
            {
                if (RentedReadBuffer != null)
                {
                    _readBufferPool.Return(RentedReadBuffer);
                    RentedReadBuffer = null;
                }

                if (RentedIntermediateBuffer != null)
                {
                    _intermediateBufferPool.Return(RentedIntermediateBuffer);
                    RentedIntermediateBuffer = null;
                }

                _readBufferPool?.Clear();
                _intermediateBufferPool?.Clear();
            }
        }

        #endregion

        #region Private Methods - Audio Processing

        /// <summary>
        /// Process audio file using margin-trimming approach to eliminate edge artifacts
        /// </summary>
        private (Dictionary<HTDemucsStem, float[,]> stems, TimeSpan duration) ProcessAudioStreaming(string inputFilePath)
        {
            using var decoder = AudioDecoderFactory.Create(
                inputFilePath,
                targetSampleRate: _options.TargetSampleRate,
                targetChannels: 2
            );

            AudioStreamInfo info = decoder.StreamInfo;
            int totalFrames = (int)(info.Duration.TotalSeconds * _options.TargetSampleRate);
            TimeSpan audioDuration = info.Duration;

            Log.Info($"Audio loaded: {totalFrames} frames, {audioDuration.TotalSeconds:F2}s");

            var stems = InitializeStemBuffers(totalFrames);

            int marginSamples = (int)(_options.MarginSeconds * _options.TargetSampleRate);
            int crossfadeSamples = (int)(_options.CrossfadeSeconds * _options.TargetSampleRate);

            int chunkSize = _modelSegmentLength > 0 ? _modelSegmentLength : (_options.ChunkSizeSeconds * _options.TargetSampleRate);

            int validSize = chunkSize - 2 * marginSamples;
            int stride = validSize - crossfadeSamples;

            if (validSize <= 0)
            {
                throw new ArgumentException($"Margin ({marginSamples}) is too large for chunk size ({chunkSize}). Valid size would be {validSize}.");
            }

            int totalChunks = (int)Math.Ceiling((double)totalFrames / stride);

            Log.Info($"Margin-trimming: chunk={chunkSize}, margin={marginSamples}, valid={validSize}, crossfade={crossfadeSamples}, stride={stride}, chunks={totalChunks}");

            using var context = new HTDemucsProcessingContext(chunkSize, crossfadeSamples);

            var audioData = ReadEntireAudio(decoder, totalFrames);

            int chunkIndex = 0;
            int targetPos = 0;

            while (targetPos < totalFrames)
            {
                int windowStart = targetPos - marginSamples;
                int windowEnd = targetPos + validSize + marginSamples;
                int windowSize = windowEnd - windowStart;

                var windowChunk = ExtractWindowWithPadding(audioData, windowStart, windowSize, totalFrames);
                var separatedStems = ProcessChunk(windowChunk, context);
                var trimmedStems = TrimMargins(separatedStems, marginSamples, validSize);

                int validLength = Math.Min(validSize, totalFrames - targetPos);

                ApplyTrimmedOverlapAdd(stems, trimmedStems, targetPos, crossfadeSamples, validLength, totalFrames);

                targetPos += stride;
                chunkIndex++;

                ReportProgress(new HTDemucsSeparationProgress
                {
                    CurrentFile = Path.GetFileName(inputFilePath),
                    Status = $"Processing chunk {chunkIndex}/{totalChunks}",
                    ProcessedChunks = chunkIndex,
                    TotalChunks = totalChunks,
                    OverallProgress = 10 + ((double)chunkIndex / totalChunks * 80)
                });
            }

            return (stems, audioDuration);
        }

        /// <summary>
        /// Initialize stem buffers for all target stems
        /// </summary>
        private Dictionary<HTDemucsStem, float[,]> InitializeStemBuffers(int totalFrames)
        {
            var stems = new Dictionary<HTDemucsStem, float[,]>();

            foreach (HTDemucsStem stem in Enum.GetValues(typeof(HTDemucsStem)))
            {
                if (stem == HTDemucsStem.All) continue;
                if (_options.TargetStems.HasFlag(stem))
                {
                    stems[stem] = new float[2, totalFrames];
                }
            }

            return stems;
        }

        /// <summary>
        /// Read entire audio into memory for context window access
        /// </summary>
        private float[,] ReadEntireAudio(IAudioDecoder decoder, int totalFrames)
        {
            var audioData = new float[2, totalFrames];
            int framesRead = 0;

            int bufferSize = 8192;
            byte[] readBuffer = new byte[bufferSize * 2 * sizeof(float)];

            while (framesRead < totalFrames)
            {
                var result = decoder.ReadFrames(readBuffer);

                if (!result.IsSucceeded || result.FramesRead == 0)
                    break;

                int bytesRead = result.FramesRead * 2 * sizeof(float);
                var floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                    readBuffer.AsSpan(0, bytesRead)
                );

                for (int i = 0; i < result.FramesRead && framesRead + i < totalFrames; i++)
                {
                    audioData[0, framesRead + i] = floatSpan[i * 2];
                    audioData[1, framesRead + i] = floatSpan[i * 2 + 1];
                }

                framesRead += result.FramesRead;
            }

            return audioData;
        }

        /// <summary>
        /// Extract window with reflection padding at boundaries
        /// </summary>
        private float[,] ExtractWindowWithPadding(float[,] audioData, int windowStart, int windowSize, int totalFrames)
        {
            var window = new float[2, windowSize];

            for (int i = 0; i < windowSize; i++)
            {
                int sourceIdx = windowStart + i;

                if (sourceIdx < 0)
                {
                    sourceIdx = -sourceIdx;
                }
                else if (sourceIdx >= totalFrames)
                {
                    sourceIdx = 2 * totalFrames - sourceIdx - 2;
                }

                sourceIdx = Math.Max(0, Math.Min(totalFrames - 1, sourceIdx));

                window[0, i] = audioData[0, sourceIdx];
                window[1, i] = audioData[1, sourceIdx];
            }

            return window;
        }

        /// <summary>
        /// Trim margins from separated stems output
        /// </summary>
        private Dictionary<HTDemucsStem, float[,]> TrimMargins(
            Dictionary<HTDemucsStem, float[,]> stems,
            int marginSamples,
            int validSize)
        {
            var trimmed = new Dictionary<HTDemucsStem, float[,]>();

            foreach (var kvp in stems)
            {
                var stem = kvp.Key;
                var source = kvp.Value;
                int sourceLength = source.GetLength(1);

                int extractLength = Math.Min(validSize, sourceLength - marginSamples);
                var trimmedStem = new float[2, extractLength];

                for (int ch = 0; ch < 2; ch++)
                {
                    for (int i = 0; i < extractLength; i++)
                    {
                        trimmedStem[ch, i] = source[ch, marginSamples + i];
                    }
                }

                trimmed[stem] = trimmedStem;
            }

            return trimmed;
        }

        /// <summary>
        /// Apply trimmed chunks with crossfade overlap
        /// </summary>
        private void ApplyTrimmedOverlapAdd(
            Dictionary<HTDemucsStem, float[,]> targetBuffers,
            Dictionary<HTDemucsStem, float[,]> sourceChunk,
            int position,
            int crossfadeSamples,
            int validLength,
            int totalLength)
        {
            foreach (var kvp in sourceChunk)
            {
                var stem = kvp.Key;
                var source = kvp.Value;

                if (!targetBuffers.ContainsKey(stem))
                    continue;

                var target = targetBuffers[stem];

                if (position == 0)
                {
                    CopyAudioRegion(source, target, 0, 0, Math.Min(validLength, totalLength));
                }
                else
                {
                    int crossfadeLength = Math.Min(crossfadeSamples, Math.Min(validLength, totalLength - position));
                    if (crossfadeLength > 0)
                    {
                        BlendOverlap(source, target, 0, position, crossfadeLength);
                    }

                    int nonOverlapStart = crossfadeLength;
                    int copyLength = Math.Min(validLength - nonOverlapStart, totalLength - position - nonOverlapStart);
                    if (copyLength > 0)
                    {
                        CopyAudioRegion(source, target, nonOverlapStart, position + nonOverlapStart, copyLength);
                    }
                }
            }
        }

        /// <summary>
        /// Pad chunk to required size
        /// </summary>
        private float[,] PadChunk(float[,] chunk, int targetSize)
        {
            int currentSize = chunk.GetLength(1);
            if (currentSize >= targetSize) return chunk;

            var padded = new float[2, targetSize];
            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < currentSize; i++)
                {
                    padded[ch, i] = chunk[ch, i];
                }
            }

            return padded;
        }

        /// <summary>
        /// Read audio data into a sliding window buffer
        /// </summary>
        private float[,] ReadAudioWindow(IAudioDecoder decoder, int windowSize, int startFrame, int totalFrames, BufferPool bufferPool)
        {
            var window = new float[2, windowSize];
            int framesRead = 0;

            int bufferSize = 8192;
            byte[] readBuffer = new byte[bufferSize * 2 * sizeof(float)];

            while (framesRead < windowSize && startFrame + framesRead < totalFrames)
            {
                var result = decoder.ReadFrames(readBuffer);

                if (!result.IsSucceeded || result.FramesRead == 0)
                    break;

                int bytesRead = result.FramesRead * 2 * sizeof(float);
                var floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                    readBuffer.AsSpan(0, bytesRead)
                );

                for (int i = 0; i < result.FramesRead && framesRead + i < windowSize; i++)
                {
                    window[0, framesRead + i] = floatSpan[i * 2];
                    window[1, framesRead + i] = floatSpan[i * 2 + 1];
                }

                framesRead += result.FramesRead;
            }

            return window;
        }

        #endregion
    }
}
