using Ownaudio.Decoders;
using Ownaudio.Engines;
using Ownaudio.Sources.Extensions;
using Ownaudio.Utilities.Extensions;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Sources;

public partial class Source : ISource
{
    /// <summary>
    /// Pre-allocated buffer for processed audio samples to avoid frequent allocations.
    /// </summary>
    private float[]? _processedSamples;

    /// <summary>
    /// Tracks the last processed buffer size to determine when reallocation is needed.
    /// </summary>
    private int _lastProcessedSize = 0;

    /// <summary>
    /// Pre-allocated buffer for SoundTouch input to avoid ToArray() allocations.
    /// </summary>
    private float[]? _soundTouchInputBuffer;

    /// <summary>
    /// Tracks the last SoundTouch input buffer size.
    /// </summary>
    private int _lastSoundTouchInputSize = 0;

    /// <summary>
    /// Pre-allocated array-based buffer to replace.
    /// </summary>
    private float[]? _soundTouchBuffer;

    /// <summary>
    /// Current count of valid samples in the SoundTouch buffer.
    /// </summary>
    private int _soundTouchBufferCount = 0;

    /// <summary>
    /// Maximum capacity of the SoundTouch buffer.
    /// </summary>
    private int _soundTouchBufferCapacity = 0;

    /// <summary>
    /// Reusable Task for state changes to avoid Task allocations.
    /// </summary>
    private Task _stateChangeTask = Task.CompletedTask;

    /// <summary>
    /// Handles audio decoder errors and attempts recovery by reinitializing the decoder.
    /// </summary>
    /// <param name="result">The failed audio decoder result containing error information.</param>
    /// <returns>
    /// <c>true</c> to continue the decoder thread after successful recovery; 
    /// <c>false</c> to break the thread if recovery fails or the source is idle.
    /// </returns>
    /// <remarks>
    /// This method performs the following recovery steps:
    /// 1. Clears all queued data and returns buffers to the pool
    /// 2. Logs the error and disposes the current decoder
    /// 3. Attempts to recreate the decoder using the original URL or stream
    /// 4. Seeks to the last known position after successful recreation
    /// If the source state becomes idle during recovery, the method returns false to terminate the thread.
    /// </remarks>
    protected virtual bool HandleDecoderError(AudioDecoderResult result)
    {
        while (Queue.TryDequeue(out _)) { }
        while (SourceSampleData.TryDequeue(out var buffer))
        {
            SimpleAudioBufferPool.Return(buffer);
        }

        Logger?.LogWarning($"Failed to decode audio frame, retrying: {result.ErrorMessage}");

        CurrentDecoder?.Dispose();
        CurrentDecoder = null;

        while (CurrentDecoder == null)
        {
            if (State == SourceState.Idle)
            {
                IsLoaded = false;
                return false;
            }

            try
            {
#nullable disable
                CurrentDecoder = CurrentUrl != null ? CreateDecoder(CurrentUrl) : CreateDecoder(CurrentStream);
                break;
#nullable restore
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Unable to recreate audio decoder, retrying: {ex.Message}");
                Thread.Sleep(1000);
            }
        }

        Logger?.LogInfo($"Audio decoder has been recreated, seeking to the last position ({Position}).");
        Seek(Position);
        return true;
    }

    /// <summary>
    /// Continuously decodes audio data during playback in a background thread.
    /// </summary>
    /// <remarks>
    /// This method runs in the decoder thread and performs the following operations:
    /// - Continuously decodes audio frames from the current decoder
    /// - Handles seeking operations by clearing queues and waiting
    /// - Manages end-of-file conditions and decoder errors
    /// - Controls queue size to prevent excessive memory usage
    /// - Coordinates with the engine thread for proper shutdown
    /// The thread continues until the source state becomes idle or an unrecoverable error occurs.
    /// </remarks>
    private void RunDecoder()
    {
        Logger?.LogInfo("Decoder thread is started.");

        while (State != SourceState.Idle)
        {
            while (IsSeeking)
            {
                if (State == SourceState.Idle)
                    break;

                if (!Queue.IsEmpty)
                    while (Queue.TryDequeue(out _)) { }

                if (!SourceSampleData.IsEmpty)
                    while (SourceSampleData.TryDequeue(out _)) { }

                Thread.Sleep(10);
            }

#nullable disable
            AudioDecoderResult result = CurrentDecoder.DecodeNextFrame();

            if (result.IsEOF)
            {
                IsEOF = true;
                EngineThread.EnsureThreadDone(() => IsSeeking);

                if (IsSeeking)
                {
                    IsEOF = false;
                    while (Queue.TryDequeue(out _)) { }
                    while (SourceSampleData.TryDequeue(out _)) { }

                    continue;
                }

                break;
            }
#nullable restore

            if (!result.IsSucceeded)
            {
                if (HandleDecoderError(result))
                    continue;

                IsEOF = true; // ends the engine thread
                break;
            }

            while (Queue.Count >= MaxQueueSize)
            {
                if (State == SourceState.Idle)
                    break;

                Thread.Sleep(100);
            }

            Queue.Enqueue(result.Frame);
            lock (lockObject)
            {
                FixedBufferSize = result.Frame.Data.Length / sizeof(float);
            }
        }

        Logger?.LogInfo("Decoder thread is completed.");
    }

    /// <summary>
    /// Continuously processes and prepares audio data for the output engine with SoundTouch effects.
    /// </summary>
    /// <remarks>
    /// This method runs in the engine thread and performs the following operations:
    /// - Configures SoundTouch for pitch and tempo modifications
    /// - Processes decoded audio frames from the queue
    /// - Applies SoundTouch effects when pitch or tempo changes are active
    /// - Manages buffering states and coordinates with the decoder thread
    /// - Handles end-of-file conditions and flushes remaining data
    /// - Sends processed audio data to the source manager for output
    /// The thread processes data in chunks based on the fixed buffer size and maintains
    /// proper synchronization between decoding and output operations.
    /// </remarks>
    private void RunEngine()
    {
        Logger?.LogInfo("Engine thread is started.");

        double calculateTime = 0;
        int numSamples = 0;
        bool isSoundtouch = true;

        EnsureProcessedSamplesBuffer();
        EnsureSoundTouchBuffers();

        soundTouch.Clear();
        soundTouch.SampleRate = SourceManager.OutputEngineOptions.SampleRate;
        soundTouch.Channels = (int)SourceManager.OutputEngineOptions.Channels;

        // Reset buffer count
        _soundTouchBufferCount = 0;

        while (State != SourceState.Idle)
        {
            if (State == SourceState.Paused || IsSeeking)
            {
                Thread.Sleep(10);
                continue;
            }

            if (SourceSampleData.Count >= MaxQueueSize)
            {
                Thread.Sleep(2);
                continue;
            }

            if (Queue.Count < MinQueueSize && !IsEOF)
            {
                SetAndRaiseStateChanged(SourceState.Buffering);
                Thread.Sleep(2);
                continue;
            }
            
            if (_soundTouchBufferCount >= FixedBufferSize)
            {
                SendDataEngineSimple(calculateTime);
            }
            else
            {
                if (Queue.TryDequeue(out var frame))
                {
                    Span<float> samples = MemoryMarshal.Cast<byte, float>(frame.Data);
                    calculateTime = frame.PresentationTime;

                    if (isSoundtouch)
                    {
                        lock (lockObject)
                        {
                            // SoundTouch requires float[] array, so we need to convert Span to array
                            // But we can reuse a pre-allocated buffer for this
                            EnsureSoundTouchInputBuffer(samples.Length);
                            samples.SafeCopyTo(_soundTouchInputBuffer.AsSpan(0, samples.Length));

                            soundTouch.PutSamples(_soundTouchInputBuffer, samples.Length / soundTouch.Channels);

                            FastClear(_processedSamples, FixedBufferSize);
                            
                            int maxFrames = FixedBufferSize / soundTouch.Channels;
                            int actualBufferSize = maxFrames * soundTouch.Channels;
                        
                            if (_processedSamples?.Length < actualBufferSize)
                            {
                                _processedSamples = new float[actualBufferSize];
                                _lastProcessedSize = actualBufferSize;
                            }

                            try
                            {
                               Span<float> outputSpan = _processedSamples.AsSpan(0, actualBufferSize);
                                numSamples = soundTouch.ReceiveSamples(outputSpan, maxFrames);

                                if (numSamples > 0)
                                {
                                    int actualSamples = numSamples * soundTouch.Channels;
                                    AddToSoundTouchBuffer(_processedSamples.AsSpan(0, actualSamples));
                                }
                            }
                            catch (ArgumentException ex)
                            {
                                Logger?.LogError($"SoundTouch ReceiveSamples error: {ex.Message}");
                                Logger?.LogError($"Buffer size: {actualBufferSize}, Max frames: {maxFrames}, Channels: {soundTouch.Channels}");
                            
                                // Fallback: bypass SoundTouch
                                AddToSoundTouchBuffer(samples);
                            }
                        }
                    }
                    else
                    {
                        AddToSoundTouchBuffer(samples);
                    }
                }
                else
                {
                    if (IsEOF)
                    {
                        // Flush SoundTouch
                        if (isSoundtouch)
                        {
                            do
                            {
                                FastClear(_processedSamples, FixedBufferSize);
                                numSamples = soundTouch.ReceiveSamples(_processedSamples, FixedBufferSize / soundTouch.Channels);

                                if (numSamples > 0)
                                {
                                    AddToSoundTouchBuffer(_processedSamples.AsSpan(0, numSamples * soundTouch.Channels));
                                    SendDataEngineSimple(calculateTime);
                                }
                            } while (numSamples > 0);
                        }
                        else if (_soundTouchBufferCount > 0)
                        {
                            SendDataEngineSimple(calculateTime);
                        }

                        Logger?.LogInfo("End of audio data.");
                        break;
                    }
                    else
                    {
                        Thread.Sleep(10);
                        continue;
                    }
                }
            }
        }

        SetAndRaisePositionChanged(TimeSpan.Zero);

        if (!_stateChangeTask.IsCompleted)
            _stateChangeTask.Wait();

        _stateChangeTask = Task.Run(() => SetAndRaiseStateChanged(SourceState.Idle));

        Logger?.LogInfo("Engine thread is completed.");
    }

    /// <summary>
    /// Ensures the processed samples buffer is allocated and sized correctly for the current buffer requirements.
    /// </summary>
    /// <remarks>
    /// This method checks if the buffer needs to be reallocated based on the current fixed buffer size.
    /// Reallocation occurs when the buffer is null or when the buffer size has changed since the last allocation.
    /// This approach minimizes memory allocations during audio processing.
    /// </remarks>
    private void EnsureProcessedSamplesBuffer()
    {
        if (_processedSamples == null || _lastProcessedSize != FixedBufferSize)
        {
            _processedSamples = new float[FixedBufferSize];
            _lastProcessedSize = FixedBufferSize;
        }
    }

    /// <summary>
    /// Ensures the SoundTouch input buffer is allocated with sufficient size.
    /// </summary>
    /// <param name="requiredSize">The minimum required size for the buffer.</param>
    private void EnsureSoundTouchInputBuffer(int requiredSize)
    {
        if (_soundTouchInputBuffer == null || _lastSoundTouchInputSize < requiredSize)
        {
            _soundTouchInputBuffer = new float[requiredSize];
            _lastSoundTouchInputSize = requiredSize;
        }
    }

    /// <summary>
    /// Ensures the SoundTouch buffer array is allocated with sufficient capacity.
    /// </summary>
    private void EnsureSoundTouchBuffers()
    {
        int requiredCapacity = FixedBufferSize * 4; // Allow for multiple frames

        if (_soundTouchBuffer == null || _soundTouchBufferCapacity < requiredCapacity)
        {
            _soundTouchBuffer = new float[requiredCapacity];
            _soundTouchBufferCapacity = requiredCapacity;
            _soundTouchBufferCount = 0;
        }
    }

    /// <summary>
    /// Adds samples to the SoundTouch buffer using array operations instead of List operations.
    /// </summary>
    /// <param name="samples">The audio samples to add to the buffer.</param>
    private void AddToSoundTouchBuffer(ReadOnlySpan<float> samples)
    {
        int availableSpace = _soundTouchBufferCapacity - _soundTouchBufferCount;
        if (samples.Length > availableSpace)
        {
            EnsureSoundTouchBuffers();
            return;
        }

        if (_soundTouchBufferCount + samples.Length > _soundTouchBufferCapacity)
        {
            int newCapacity = Math.Max(_soundTouchBufferCapacity * 2, _soundTouchBufferCount + samples.Length);
            var newBuffer = new float[newCapacity];

            if (_soundTouchBufferCount > 0)
            {
                _soundTouchBuffer.AsSpan(0, _soundTouchBufferCount).SafeCopyTo(newBuffer);
            }

            _soundTouchBuffer = newBuffer;
            _soundTouchBufferCapacity = newCapacity;
        }

        samples.SafeCopyTo(_soundTouchBuffer.AsSpan(_soundTouchBufferCount, samples.Length));
        _soundTouchBufferCount += samples.Length;
    }

#nullable disable
    /// <summary>
    /// Prepares processed audio data for the source manager and updates playback position.
    /// </summary>
    /// <param name="calculateTime">Current presentation time in milliseconds for position tracking.</param>
    /// <remarks>
    /// This method performs the following operations:
    /// - Splits the buffer into chunks based on frames per buffer and channel count
    /// - Uses buffer pooling for larger sample arrays to reduce garbage collection
    /// - Applies sample processors (volume, custom effects) to each chunk
    /// - Calculates and updates the current playback position
    /// - Enqueues processed samples for output to the audio engine
    /// The method ensures proper buffer management by returning pooled buffers in the finally block.
    /// </remarks>
    private void SendDataEngineSimple(double calculateTime)
    {
        int samplesSize = FramesPerBuffer * CurrentDecoder.StreamInfo.Channels;
        int chunksToProcess = _soundTouchBufferCount / samplesSize;

        for (int i = 0; i < chunksToProcess; i++)
        {
            float[] samples = samplesSize > 512
                ? SimpleAudioBufferPool.Rent(samplesSize)
                : new float[samplesSize];

            try
            {
                int sourceIndex = i * samplesSize;

                if (sourceIndex + samplesSize <= _soundTouchBufferCount &&
                    samplesSize <= samples.Length)
                {
                    _soundTouchBuffer.AsSpan(sourceIndex, samplesSize).SafeCopyTo(samples.AsSpan(0, samplesSize));

                    ProcessSampleProcessors(samples.AsSpan(0, samplesSize));
                    SourceSampleData.Enqueue(samples);

                    if (CurrentDecoder is not null)
                        calculateTime += (samplesSize / (double)(CurrentDecoder.StreamInfo.SampleRate * CurrentDecoder.StreamInfo.Channels)) * 1000;

                    SetAndRaisePositionChanged(TimeSpan.FromMilliseconds(calculateTime));

                    samples = null; // Will be managed by the pool or GC
                }
                else
                {
                    Logger?.LogWarning($"SoundTouch buffer underflow: {sourceIndex + samplesSize} > {_soundTouchBufferCount}");
                }
            }
            finally
            {
                if (samples != null && samplesSize > 512)
                    SimpleAudioBufferPool.Return(samples);
            }
        }

        int processedSamples = chunksToProcess * samplesSize;
        if (processedSamples > 0)
        {
            int remainingSamples = _soundTouchBufferCount - processedSamples;
            if (remainingSamples > 0)
            {
                _soundTouchBuffer.AsSpan(processedSamples, remainingSamples)
                    .SafeCopyTo(_soundTouchBuffer.AsSpan(0, remainingSamples));
            }
            _soundTouchBufferCount = remainingSamples;
        }
    }

    /// <summary>
    /// Efficiently clears a float array buffer using optimized methods based on buffer size.
    /// </summary>
    /// <param name="buffer">The float array buffer to clear.</param>
    /// <param name="length">The number of elements to clear from the start of the buffer.</param>
    /// <remarks>
    /// This method uses size-based optimization:
    /// - For buffers ≤1024 elements: Uses Span.Clear() which is optimized for smaller buffers
    /// - For larger buffers: Uses Array.Clear() which is more efficient for larger memory blocks
    /// This approach provides better performance across different buffer sizes.
    /// </remarks>
    private static void FastClear(float[] buffer, int length)
    {
        if (buffer == null) return;

        int clearLength = Math.Min(buffer.Length, length);

        if (clearLength <= 1024)
            buffer.AsSpan(0, clearLength).Clear();
        else
            Array.Clear(buffer, 0, clearLength);
    }

    /// <summary>
    /// Decodes and returns the complete audio content as a byte array.
    /// </summary>
    /// <param name="position">
    /// The position to seek to after decoding all data, typically TimeSpan.Zero for the beginning of the file.
    /// </param>
    /// <returns>
    /// A byte array containing the complete decoded audio data, or null if no audio is loaded.
    /// </returns>
    /// <remarks>
    /// This method decodes the entire audio file into memory at once. Use with caution for large files
    /// as it may consume significant memory. The position parameter allows seeking to a specific
    /// position after the decoding operation is complete.
    /// </remarks>
    public byte[] GetByteAudioData(TimeSpan position)
    {
        if (IsLoaded)
        {
            AudioDecoderResult result = CurrentDecoder.DecodeAllFrames(position);
            return result.Frame.Data;
        }
        else
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes and returns the complete audio content as a float array.
    /// </summary>
    /// <param name="position">
    /// The position to seek to after decoding all data, typically TimeSpan.Zero for the beginning of the file.
    /// </param>
    /// <returns>
    /// A float array containing the complete decoded audio data converted from bytes, or null if no audio is loaded.
    /// </returns>
    /// <remarks>
    /// This method decodes the entire audio file into memory and converts the byte data to float samples.
    /// Use with caution for large files as it may consume significant memory. The returned float array
    /// contains the raw audio samples that can be used for direct audio processing or analysis.
    /// </remarks>
    public float[] GetFloatAudioData(TimeSpan position)
    {
        if (IsLoaded)
        {
            AudioDecoderResult result = CurrentDecoder.DecodeAllFrames(position);
            Span<float> audioData = MemoryMarshal.Cast<byte, float>(result.Frame.Data);
            return audioData.ToArray();
        }
        else
        {
            return null;
        }
    }
}
