using Ownaudio.Decoders;
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
    /// Pre-allocated buffer for storing processed audio samples from SoundTouch.
    /// This buffer is reused to minimize memory allocations during audio processing.
    /// </summary>
    private float[]? _soundTouchProcessedBuffer;

    /// <summary>
    /// Pre-allocated temporary buffer for SoundTouch operations.
    /// Used as an intermediate buffer during audio processing to reduce garbage collection pressure.
    /// </summary>
    private float[]? _soundTouchTempBuffer;

    /// <summary>
    /// Circular buffer for managing SoundTouch processed audio data.
    /// Provides efficient FIFO storage for audio samples with configurable capacity.
    /// </summary>
    private CircularAudioBuffer? _soundTouchCircularBuffer;

    /// <summary>
    /// Flag indicating whether the SoundTouch buffers have been properly initialized.
    /// Used to prevent multiple initialization and ensure thread-safe setup.
    /// </summary>
    private bool _buffersInitialized = false;

    /// <summary>
    /// Performance counter tracking successful buffer pool retrievals.
    /// Used for debugging and performance analysis in debug builds.
    /// </summary>
    private int _bufferPoolHits = 0;

    /// <summary>
    /// Performance counter tracking failed buffer pool retrievals (new allocations).
    /// Used for debugging and performance analysis in debug builds.
    /// </summary>
    private int _bufferPoolMisses = 0;

    /// <summary>
    /// Initializes reusable buffers for SoundTouch processing with thread-safe double-checked locking.
    /// Sets up pre-allocated buffers and circular buffer to minimize memory allocations during playback.
    /// </summary>
    /// <remarks>
    /// This method:
    /// - Creates buffers 4 times larger than the fixed buffer size for efficiency
    /// - Sets up a circular buffer with 10 seconds of audio capacity
    /// - Uses double-checked locking pattern for thread safety
    /// - Buffers are obtained from the AudioBufferPool for memory efficiency
    /// </remarks>
    private void InitializeSoundTouchBuffers()
    {
        if (_buffersInitialized) return;

        lock (lockObject)
        {
            if (_buffersInitialized) return;

            int bufferSize = FixedBufferSize * 4; // Larger buffer for better efficiency
            _soundTouchProcessedBuffer = AudioBufferPool.Rent(bufferSize);
            _soundTouchTempBuffer = AudioBufferPool.Rent(bufferSize);

            // Circular buffer with capacity for 10 seconds of audio
            int circularCapacity = SourceManager.OutputEngineOptions.SampleRate *
                                 (int)SourceManager.OutputEngineOptions.Channels * 10;
            _soundTouchCircularBuffer = new CircularAudioBuffer(circularCapacity);

            _buffersInitialized = true;
        }
    }

    /// <summary>
    /// Performs cleanup of SoundTouch buffers and returns them to the buffer pool.
    /// This method is thread-safe and ensures proper resource cleanup.
    /// </summary>
    /// <remarks>
    /// Cleanup process:
    /// - Returns pre-allocated buffers to the AudioBufferPool
    /// - Clears the circular buffer
    /// - Resets initialization flag
    /// - Uses locking to ensure thread-safe cleanup
    /// </remarks>
    private void DisposeSoundTouchBuffers()
    {
        lock (lockObject)
        {
            if (_soundTouchProcessedBuffer != null)
            {
                AudioBufferPool.Return(_soundTouchProcessedBuffer);
                _soundTouchProcessedBuffer = null;
            }

            if (_soundTouchTempBuffer != null)
            {
                AudioBufferPool.Return(_soundTouchTempBuffer);
                _soundTouchTempBuffer = null;
            }

            _soundTouchCircularBuffer?.Clear();
            _buffersInitialized = false;
        }
    }

    /// <summary>
    /// Handles audio decoder errors with automatic recovery mechanisms.
    /// Attempts to recreate the decoder and restore playback from the last position.
    /// </summary>
    /// <param name="result">The failed audio decoder result containing error information.</param>
    /// <returns>
    /// <c>true</c> if the decoder was successfully recreated and the decoder thread should continue;
    /// <c>false</c> if recovery failed and the decoder thread should terminate.
    /// </returns>
    /// <remarks>
    /// Recovery process:
    /// - Clears all audio buffers efficiently
    /// - Disposes the current decoder
    /// - Attempts to recreate decoder using original URL or stream
    /// - Retries with 1-second intervals on failure
    /// - Seeks to the last known position after successful recreation
    /// - Returns false if the source state becomes idle during recovery
    /// </remarks>
    protected virtual bool HandleDecoderError(AudioDecoderResult result)
    {
        // Clear all buffers efficiently
        ClearAllBuffers();

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
#nullable restore
                break;
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
    /// Efficiently clears all internal audio buffers and returns pooled buffers to the pool.
    /// This method ensures proper resource cleanup and prevents memory leaks.
    /// </summary>
    /// <remarks>
    /// Clearing process:
    /// - Drains the audio frame queue completely
    /// - Drains the source sample data queue and returns buffers to pool
    /// - Clears SoundTouch processor state
    /// - Clears circular buffer with thread-safe locking
    /// </remarks>
    private void ClearAllBuffers()
    {
        // Clear queues efficiently
        while (Queue.TryDequeue(out _)) { }
        while (SourceSampleData.TryDequeue(out var buffer))
        {
            // Return dequeued buffers to pool
            AudioBufferPool.Return(buffer);
        }

        // Clear SoundTouch and circular buffer
        lock (lockObject)
        {
            soundTouch.Clear();
            _soundTouchCircularBuffer?.Clear();
        }
    }

    /// <summary>
    /// Main decoder thread method that continuously decodes audio frames during playback.
    /// Handles frame decoding, queue management, seeking, and error recovery.
    /// </summary>
    /// <remarks>
    /// Thread responsibilities:
    /// - Continuously decodes audio frames from the current decoder
    /// - Manages audio frame queue size within specified limits
    /// - Handles seeking operations by clearing buffers
    /// - Processes end-of-file conditions
    /// - Manages error recovery through HandleDecoderError
    /// - Updates buffer size based on decoded frame data
    /// - Uses Thread.Yield() for better responsiveness than Sleep()
    /// </remarks>
    private void RunDecoder()
    {
        Logger?.LogInfo("Decoder thread is started.");

        while (State != SourceState.Idle)
        {
            // Fast seeking check
            while (IsSeeking)
            {
                if (State == SourceState.Idle)
                    break;

                // Efficient queue clearing with buffer pool return
                ClearQueuesWithPoolReturn();
                Thread.Yield(); // Better than Sleep(10)
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
                    ClearQueuesWithPoolReturn();
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

            // Efficient queue size check
            while (Queue.Count >= MaxQueueSize)
            {
                if (State == SourceState.Idle)
                    break;

                Thread.Yield(); // More responsive than Sleep(100)
            }

            Queue.Enqueue(result.Frame);

            // Update buffer size atomically
            lock (lockObject)
            {
                FixedBufferSize = result.Frame.Data.Length / sizeof(float);
            }
        }

        Logger?.LogInfo("Decoder thread is completed.");
    }

    /// <summary>
    /// Efficiently clears audio queues and returns pooled buffers to the AudioBufferPool.
    /// This method prevents memory leaks by properly managing pooled buffer lifecycle.
    /// </summary>
    /// <remarks>
    /// This method:
    /// - Drains the audio frame queue if not empty
    /// - Drains the source sample data queue and returns each buffer to the pool
    /// - Uses efficient queue checking to avoid unnecessary operations
    /// - Ensures proper buffer pool management
    /// </remarks>
    private void ClearQueuesWithPoolReturn()
    {
        if (!Queue.IsEmpty)
        {
            while (Queue.TryDequeue(out _)) { }
        }

        if (!SourceSampleData.IsEmpty)
        {
            while (SourceSampleData.TryDequeue(out var buffer))
            {
                AudioBufferPool.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Main engine thread method that processes audio data and prepares it for output.
    /// Handles SoundTouch processing, buffer management, and audio output preparation.
    /// </summary>
    /// <remarks>
    /// Engine thread responsibilities:
    /// - Initializes SoundTouch buffers and configuration
    /// - Manages playback state transitions (playing, paused, buffering)
    /// - Processes audio through SoundTouch when pitch/tempo changes are needed
    /// - Manages circular buffer for efficient audio data flow
    /// - Handles end-of-file conditions and buffer flushing
    /// - Updates playback position and raises position changed events
    /// - Uses non-blocking state changes for better performance
    /// </remarks>
    private void RunEngine()
    {
        Logger?.LogInfo("Engine thread is started.");

        // Initialize buffers
        InitializeSoundTouchBuffers();

        double calculateTime = 0;
        bool isSoundtouch = Pitch != 0 || Tempo != 0;

        // Configure SoundTouch
        lock (lockObject)
        {
            soundTouch.Clear();
            soundTouch.SampleRate = SourceManager.OutputEngineOptions.SampleRate;
            soundTouch.Channels = (int)SourceManager.OutputEngineOptions.Channels;
        }

        while (State != SourceState.Idle)
        {
            if (State == SourceState.Paused || IsSeeking)
            {
                Thread.Yield();
                continue;
            }

            if (SourceSampleData.Count >= MaxQueueSize)
            {
                Thread.Yield();
                continue;
            }

            if (Queue.Count < MinQueueSize && !IsEOF)
            {
                SetAndRaiseStateChanged(SourceState.Buffering);
                Thread.Yield();
                continue;
            }

            // Check if we have enough data in circular buffer
            if (_soundTouchCircularBuffer?.Count >= FixedBufferSize)
            {
                SendDataEngineOptimized(calculateTime);
            }
            else
            {
                if (Queue.TryDequeue(out var frame))
                {
                    Span<float> samples = MemoryMarshal.Cast<byte, float>(frame.Data);
                    calculateTime = frame.PresentationTime;

                    if (isSoundtouch)
                    {
                        ProcessWithSoundTouchOptimized(samples);
                    }
                    else
                    {
                        // Direct processing without SoundTouch
                        _soundTouchCircularBuffer?.AddSamples(samples);
                    }
                }
                else
                {
                    if (IsEOF)
                    {
                        // Flush remaining SoundTouch data efficiently
                        if (isSoundtouch)
                        {
                            FlushSoundTouchBuffer(calculateTime);
                        }
                        else if (_soundTouchCircularBuffer?.Count > 0)
                        {
                            SendDataEngineOptimized(calculateTime);
                        }

                        Logger?.LogInfo("End of audio data and all buffers are empty.");
                        break;
                    }
                    else
                    {
                        Logger?.LogInfo("Waiting for more data from the decoder...");
                        Thread.Yield();
                        continue;
                    }
                }
            }
        }

        SetAndRaisePositionChanged(TimeSpan.Zero);

        // Non-blocking state change
        Task.Run(() => SetAndRaiseStateChanged(SourceState.Idle));

        Logger?.LogInfo("Engine thread is completed.");
    }

    /// <summary>
    /// Processes audio samples through SoundTouch using pre-allocated buffers for optimal performance.
    /// Minimizes memory allocations by reusing buffers and efficiently manages SoundTouch processing.
    /// </summary>
    /// <param name="samples">The input audio samples to process through SoundTouch.</param>
    /// <remarks>
    /// Processing steps:
    /// - Converts samples to array format required by SoundTouch API
    /// - Feeds samples into SoundTouch processor
    /// - Retrieves processed samples using pre-allocated buffer
    /// - Adds processed samples to circular buffer for output
    /// - Uses thread-safe locking during SoundTouch operations
    /// - Clears buffer before processing to ensure clean output
    /// </remarks>
    private void ProcessWithSoundTouchOptimized(ReadOnlySpan<float> samples)
    {
        lock (lockObject)
        {
            int numSamples = 0;

            // Put samples into SoundTouch
            var samplesArray = samples.ToArray(); // Minimal allocation for SoundTouch API
            soundTouch.PutSamples(samplesArray, samples.Length / soundTouch.Channels);

            // Process with reusable buffer
            if (_soundTouchProcessedBuffer != null)
            {
                Array.Clear(_soundTouchProcessedBuffer, 0, FixedBufferSize);
                numSamples = soundTouch.ReceiveSamples(_soundTouchProcessedBuffer,
                                                          FixedBufferSize / soundTouch.Channels);
            }

            if (numSamples > 0)
            {
                int totalSamples = numSamples * soundTouch.Channels;
                var processedSpan = _soundTouchProcessedBuffer.AsSpan(0, totalSamples);
                _soundTouchCircularBuffer?.AddSamples(processedSpan);
            }
        }
    }

    /// <summary>
    /// Efficiently flushes remaining audio data from SoundTouch buffers during end-of-file processing.
    /// Ensures all processed audio data is extracted and sent to the output engine.
    /// </summary>
    /// <param name="calculateTime">The current playback time for position tracking.</param>
    /// <remarks>
    /// Flushing process:
    /// - Repeatedly extracts samples from SoundTouch until none remain
    /// - Uses pre-allocated buffers to minimize allocations
    /// - Adds extracted samples to circular buffer
    /// - Sends complete buffers to output engine immediately
    /// - Updates playback position during the process
    /// - Uses thread-safe locking for SoundTouch operations
    /// </remarks>
    private void FlushSoundTouchBuffer(double calculateTime)
    {
        lock (lockObject)
        {
            int numSamples = 0;

            if (_soundTouchProcessedBuffer != null)
            {
                Array.Clear(_soundTouchProcessedBuffer, 0, FixedBufferSize);
                numSamples = soundTouch.ReceiveSamples(_soundTouchProcessedBuffer,
                                                          FixedBufferSize / soundTouch.Channels);
            }

            while (numSamples > 0)
            {
                int totalSamples = numSamples * soundTouch.Channels;
                var processedSpan = _soundTouchProcessedBuffer.AsSpan(0, totalSamples);
                _soundTouchCircularBuffer?.AddSamples(processedSpan);

                SendDataEngineOptimized(calculateTime);

                if (_soundTouchProcessedBuffer != null)
                {
                    Array.Clear(_soundTouchProcessedBuffer, 0, FixedBufferSize);
                    numSamples = soundTouch.ReceiveSamples(_soundTouchProcessedBuffer,
                                                      FixedBufferSize / soundTouch.Channels);
                }
            }
        }
    }

    /// <summary>
    /// Optimized method for preparing audio data for the source manager using efficient buffer pool management.
    /// Extracts samples from circular buffer, processes them, and enqueues for output mixing.
    /// </summary>
    /// <param name="calculateTime">The current playback time used for position calculations and updates.</param>
    /// <remarks>
    /// Optimization features:
    /// - Uses AudioBufferPool for efficient buffer management
    /// - Extracts samples in fixed-size chunks for consistent performance
    /// - Processes samples through configured sample processors
    /// - Calculates and updates playback position accurately
    /// - Implements proper buffer lifecycle management (pool → queue → consumer)
    /// - Uses try-finally pattern to ensure buffers are returned to pool on errors
    /// - Transfers buffer ownership to queue to prevent double-return to pool
    /// </remarks>
    private void SendDataEngineOptimized(double calculateTime)
    {
#nullable disable
        int samplesSize = FramesPerBuffer * CurrentDecoder.StreamInfo.Channels;

        // Extract data from circular buffer
        while (_soundTouchCircularBuffer?.Count >= samplesSize)
        {
            // Get buffer from pool
            float[] samples = AudioBufferPool.Rent(samplesSize);

            try
            {
                // Extract samples from circular buffer
                int extractedCount = _soundTouchCircularBuffer.ExtractSamples(samples.AsSpan(0, samplesSize));

                if (extractedCount == samplesSize)
                {
                    // Process sample processors
                    ProcessSampleProcessors(samples.AsSpan(0, samplesSize));

                    // Enqueue for mixing (buffer will be returned to pool by consumer)
                    SourceSampleData.Enqueue(samples);

                    // Update time calculation
                    if (CurrentDecoder is not null)
                    {
                        calculateTime += (samplesSize / (double)(CurrentDecoder.StreamInfo.SampleRate * CurrentDecoder.StreamInfo.Channels)) * 1000;
                    }

                    SetAndRaisePositionChanged(TimeSpan.FromMilliseconds(calculateTime));

                    // Don't return buffer to pool here - it's in the queue now
                    samples = null; // Prevent return in finally block
                }
                else
                {
                    // Not enough data extracted, break and wait for more
                    break;
                }
            }
            finally
            {
                // Return buffer to pool only if we didn't enqueue it
                if (samples != null)
                {
                    AudioBufferPool.Return(samples);
                }
            }
#nullable restore
        }
    }

    /// <summary>
    /// Decodes and returns the complete audio file content as a byte array.
    /// This method processes the entire audio stream and seeks to the specified position afterwards.
    /// </summary>
    /// <param name="position">
    /// The position to seek to after decoding all audio data. 
    /// Typically set to TimeSpan.Zero to return to the beginning of the file.
    /// </param>
    /// <returns>
    /// A byte array containing the complete decoded audio data, 
    /// or an empty array if no audio is loaded or decoding fails.
    /// </returns>
    /// <remarks>
    /// This method:
    /// - Requires audio to be loaded and decoder to be available
    /// - Decodes the entire audio stream in one operation
    /// - Returns raw audio data in the decoder's native format
    /// - Automatically handles null checks and error conditions
    /// - Is useful for audio analysis or export operations
    /// </remarks>
    public byte[] GetByteAudioData(TimeSpan position)
    {
        if (IsLoaded && CurrentDecoder != null)
        {
            AudioDecoderResult result = CurrentDecoder.DecodeAllFrames(position);
            return result.Frame?.Data ?? Array.Empty<byte>();
        }

        return Array.Empty<byte>();
    }

    /// <summary>
    /// Decodes and returns the complete audio file content as a float array.
    /// This method processes the entire audio stream and converts byte data to float samples.
    /// </summary>
    /// <param name="position">
    /// The position to seek to after decoding all audio data.
    /// Typically set to TimeSpan.Zero to return to the beginning of the file.
    /// </param>
    /// <returns>
    /// A float array containing the complete decoded audio data as normalized samples,
    /// or an empty array if no audio is loaded or decoding fails.
    /// </returns>
    /// <remarks>
    /// This method:
    /// - Requires audio to be loaded and decoder to be available
    /// - Decodes the entire audio stream and converts to float samples
    /// - Uses MemoryMarshal for efficient byte-to-float conversion
    /// - Returns normalized audio samples suitable for processing
    /// - Automatically handles null checks and error conditions
    /// - Is useful for audio analysis, visualization, or processing operations
    /// </remarks>
    public float[] GetFloatAudioData(TimeSpan position)
    {
        if (IsLoaded && CurrentDecoder != null)
        {
            AudioDecoderResult result = CurrentDecoder.DecodeAllFrames(position);
            if (result.Frame?.Data != null)
            {
                Span<float> audioData = MemoryMarshal.Cast<byte, float>(result.Frame.Data);
                return audioData.ToArray();
            }
        }

        return Array.Empty<float>();
    }
}
