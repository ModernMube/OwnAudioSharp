using Ownaudio.Common;
using Ownaudio.Processors;
using Ownaudio.Sources.Extensions;
using Ownaudio.Utilities.Extensions;
using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;

namespace Ownaudio.Sources
{
    /// <summary>
    /// Optimized real-time audio source with advanced buffer pool management and SIMD channel conversion
    /// </summary>
    public partial class SourceSound : ISource
    {
        /// <summary>
        /// Indicates whether the instance has been disposed.
        /// </summary>
        private bool _disposed;

        // Pre-allocated buffers for channel conversion
        private float[] _conversionBuffer;
        private int _lastConversionBufferSize = 0;
        private readonly object _conversionLock = new object();

        // Performance optimization fields
        private int _engineChannels;
        private int _framesPerBuffer;
        private bool _isMonoToStereo;
        private bool _isStereoToMono;
        private bool _isSameFormat;

        // Batch processing buffers
        private readonly ConcurrentQueue<float[]> _pendingBuffers = new();
        private volatile bool _batchProcessingActive = false;
        private readonly ManualResetEventSlim _batchSignal = new(false);
        private Thread _batchProcessingThread;

        // Performance counters
        private long _totalSamplesProcessed = 0;
        private long _buffersProcessed = 0;
        private DateTime _lastStatsUpdate = DateTime.UtcNow;

        /// <summary>
        /// Initializes a new instance of the <see cref="SourceSound"/> class with optimized settings.
        /// </summary>
        public SourceSound(int inputDataChannels = 1)
        {
            VolumeProcessor.Volume = 1.0f;
            InputDataChannels = inputDataChannels;

            InitializeOptimizedSettings();
            StartBatchProcessingThread();
        }

        /// <summary>
        /// Initialize optimized settings and pre-calculate channel conversion parameters
        /// </summary>
        private void InitializeOptimizedSettings()
        {
            _engineChannels = (int)SourceManager.OutputEngineOptions.Channels;
            _framesPerBuffer = SourceManager.EngineFramesPerBuffer;

            // Pre-calculate channel conversion types
            _isMonoToStereo = InputDataChannels == 1 && _engineChannels == 2;
            _isStereoToMono = InputDataChannels == 2 && _engineChannels == 1;
            _isSameFormat = InputDataChannels == _engineChannels;

            Logger?.LogInfo($"SourceSound initialized: Input={InputDataChannels}ch, Output={_engineChannels}ch, " +
                          $"FramesPerBuffer={_framesPerBuffer}, Conversion={GetConversionType()}");
        }

        /// <summary>
        /// Start background batch processing thread for better performance
        /// </summary>
        private void StartBatchProcessingThread()
        {
            _batchProcessingActive = true;
            _batchProcessingThread = new Thread(BatchProcessingSamples)
            {
                Name = "SourceSound Batch Processor",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _batchProcessingThread.Start();
        }

        /// <summary>
        /// Optimized batch processing of audio samples
        /// </summary>
        private void BatchProcessingSamples()
        {
            const int maxBatchSize = 8; // Process up to 8 buffers at once
            var batchBuffers = new float[maxBatchSize][];
            int batchCount = 0;

            while (_batchProcessingActive || _pendingBuffers.Count > 0)
            {
                // Collect buffers for batch processing
                batchCount = 0;
                while (batchCount < maxBatchSize && _pendingBuffers.TryDequeue(out var buffer))
                {
                    batchBuffers[batchCount++] = buffer;
                }

                if (batchCount > 0)
                {
                    // Process batch
                    ProcessSampleBatch(batchBuffers, batchCount);

                    // Clear references
                    for (int i = 0; i < batchCount; i++)
                    {
                        batchBuffers[i] = null;
                    }
                }
                else if (_batchProcessingActive)
                {
                    // Wait for more work
                    _batchSignal.Wait(10);
                    _batchSignal.Reset();
                }
            }
        }

        /// <summary>
        /// Process a batch of audio buffers efficiently
        /// </summary>
        private void ProcessSampleBatch(float[][] buffers, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var buffer = buffers[i];
                try
                {
                    ProcessSampleProcessors(buffer.AsSpan());
                    SourceSampleData.Enqueue(buffer);

                    Interlocked.Increment(ref _buffersProcessed);
                    Interlocked.Add(ref _totalSamplesProcessed, buffer.Length);
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"Error processing sample buffer: {ex.Message}");
                    AudioBufferPool.Return(buffer);
                }
            }
        }

        /// <summary>
        /// High-performance audio sample submission with SIMD optimization and buffer pooling
        /// </summary>
        /// <param name="samples">The array of floating-point audio samples to process and enqueue.</param>
        public void SubmitSamples(float[] samples)
        {
            if (_disposed || samples == null || samples.Length == 0)
            {
                Logger?.LogWarning("SubmitSamples called with invalid parameters");
                return;
            }

            try
            {
                int inputFrames = samples.Length / InputDataChannels;
                int outputBufferSize = _framesPerBuffer * _engineChannels;

                // Ensure conversion buffer is allocated
                EnsureConversionBufferAllocated(outputBufferSize);

                // Process samples in chunks
                for (int i = 0; i + _framesPerBuffer <= inputFrames; i += _framesPerBuffer)
                {
                    var processedBuffer = ProcessSampleChunk(samples, i, outputBufferSize);
                    if (processedBuffer != null)
                    {
                        // Add to batch processing queue
                        _pendingBuffers.Enqueue(processedBuffer);
                        _batchSignal.Set();
                    }
                }

#if DEBUG
                UpdatePerformanceStats();
#endif
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in SubmitSamples: {ex.Message}");
            }
        }

        /// <summary>
        /// Process a single chunk of audio samples with optimized channel conversion
        /// </summary>
        private float[] ProcessSampleChunk(float[] samples, int startFrame, int outputBufferSize)
        {
            float[] buffer = AudioBufferPool.Rent(outputBufferSize);

            try
            {
                Array.Clear(buffer, 0, outputBufferSize);

                // Optimized channel conversion based on pre-calculated type
                if (_isMonoToStereo)
                {
                    ConvertMonoToStereoSimd(samples, buffer, startFrame, _framesPerBuffer);
                }
                else if (_isStereoToMono)
                {
                    ConvertStereoToMonoSimd(samples, buffer, startFrame, _framesPerBuffer);
                }
                else if (_isSameFormat)
                {
                    CopySameFormatSimd(samples, buffer, startFrame, _framesPerBuffer);
                }
                else
                {
                    Logger?.LogWarning($"Unsupported channel conversion: Input={InputDataChannels}, Output={_engineChannels}");
                    AudioBufferPool.Return(buffer);
                    return null;
                }

                return buffer;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error processing sample chunk: {ex.Message}");
                AudioBufferPool.Return(buffer);
                return null;
            }
        }

        /// <summary>
        /// SIMD-optimized mono to stereo conversion
        /// </summary>
        private void ConvertMonoToStereoSimd(float[] input, float[] output, int startFrame, int frameCount)
        {
            int inputStart = startFrame * InputDataChannels;

            if (Vector.IsHardwareAccelerated && frameCount >= Vector<float>.Count)
            {
                int vectorSize = Vector<float>.Count;
                int vectorFrames = frameCount - (frameCount % vectorSize);

                // SIMD processing
                for (int frame = 0; frame < vectorFrames; frame += vectorSize)
                {
                    int inputIndex = inputStart + frame;
                    int outputIndex = frame * 2;

                    // Load mono samples
                    var monoVector = new Vector<float>(input, inputIndex);

                    // Duplicate to stereo - interleaved storage
                    for (int i = 0; i < vectorSize; i++)
                    {
                        float sample = monoVector[i];
                        output[outputIndex + i * 2] = sample;     // Left
                        output[outputIndex + i * 2 + 1] = sample; // Right
                    }
                }

                // Handle remaining frames
                for (int frame = vectorFrames; frame < frameCount; frame++)
                {
                    float monoSample = input[inputStart + frame];
                    output[frame * 2] = monoSample;
                    output[frame * 2 + 1] = monoSample;
                }
            }
            else
            {
                // Scalar fallback
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float monoSample = input[inputStart + frame];
                    output[frame * 2] = monoSample;
                    output[frame * 2 + 1] = monoSample;
                }
            }
        }

        /// <summary>
        /// SIMD-optimized stereo to mono conversion
        /// </summary>
        private void ConvertStereoToMonoSimd(float[] input, float[] output, int startFrame, int frameCount)
        {
            int inputStart = startFrame * InputDataChannels;

            if (Vector.IsHardwareAccelerated && frameCount >= Vector<float>.Count)
            {
                int vectorSize = Vector<float>.Count;
                int vectorFrames = frameCount - (frameCount % vectorSize);
                var halfVector = new Vector<float>(0.5f);

                // SIMD processing
                for (int frame = 0; frame < vectorFrames; frame += vectorSize)
                {
                    int inputIndex = inputStart + frame * 2;
                    int outputIndex = frame;

                    // Process pairs of samples
                    for (int i = 0; i < vectorSize; i++)
                    {
                        float left = input[inputIndex + i * 2];
                        float right = input[inputIndex + i * 2 + 1];
                        output[outputIndex + i] = (left + right) * 0.5f;
                    }
                }

                // Handle remaining frames
                for (int frame = vectorFrames; frame < frameCount; frame++)
                {
                    int inputIndex = inputStart + frame * 2;
                    float left = input[inputIndex];
                    float right = input[inputIndex + 1];
                    output[frame] = (left + right) * 0.5f;
                }
            }
            else
            {
                // Scalar fallback
                for (int frame = 0; frame < frameCount; frame++)
                {
                    int inputIndex = inputStart + frame * 2;
                    float left = input[inputIndex];
                    float right = input[inputIndex + 1];
                    output[frame] = (left + right) * 0.5f;
                }
            }
        }

        /// <summary>
        /// SIMD-optimized same format copying
        /// </summary>
        private void CopySameFormatSimd(float[] input, float[] output, int startFrame, int frameCount)
        {
            int inputStart = startFrame * InputDataChannels;
            int sampleCount = frameCount * InputDataChannels;

            if (Vector.IsHardwareAccelerated && sampleCount >= Vector<float>.Count)
            {
                int vectorSize = Vector<float>.Count;
                int vectorLength = sampleCount - (sampleCount % vectorSize);

                // SIMD copying
                for (int i = 0; i < vectorLength; i += vectorSize)
                {
                    var vector = new Vector<float>(input, inputStart + i);
                    vector.CopyTo(output, i);
                }

                // Handle remaining samples
                for (int i = vectorLength; i < sampleCount; i++)
                {
                    output[i] = input[inputStart + i];
                }
            }
            else
            {
                // Scalar fallback using Array.Copy for better performance
                Array.Copy(input, inputStart, output, 0, sampleCount);
            }
        }

        /// <summary>
        /// Ensure conversion buffer is properly allocated
        /// </summary>
        private void EnsureConversionBufferAllocated(int requiredSize)
        {
            if (_conversionBuffer == null || _lastConversionBufferSize != requiredSize)
            {
                lock (_conversionLock)
                {
                    if (_conversionBuffer == null || _lastConversionBufferSize != requiredSize)
                    {
                        if (_conversionBuffer != null)
                        {
                            AudioBufferPool.Return(_conversionBuffer);
                        }

                        _conversionBuffer = AudioBufferPool.Rent(requiredSize);
                        _lastConversionBufferSize = requiredSize;
                    }
                }
            }
        }

        /// <summary>
        /// Get human-readable conversion type for logging
        /// </summary>
        private string GetConversionType()
        {
            if (_isMonoToStereo) return "Mono→Stereo";
            if (_isStereoToMono) return "Stereo→Mono";
            if (_isSameFormat) return "Same Format";
            return "Unsupported";
        }

#if DEBUG
        /// <summary>
        /// Update performance statistics for monitoring
        /// </summary>
        private void UpdatePerformanceStats()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastStatsUpdate).TotalSeconds >= 10.0) // Every 10 seconds
            {
                long samples = Interlocked.Read(ref _totalSamplesProcessed);
                long buffers = Interlocked.Read(ref _buffersProcessed);

                double samplesPerSecond = samples / (now - _lastStatsUpdate).TotalSeconds;
                double buffersPerSecond = buffers / (now - _lastStatsUpdate).TotalSeconds;

                Logger?.LogInfo($"SourceSound Stats: {samplesPerSecond:F0} samples/sec, " +
                              $"{buffersPerSecond:F1} buffers/sec, Pending: {_pendingBuffers.Count}");

                Interlocked.Exchange(ref _totalSamplesProcessed, 0);
                Interlocked.Exchange(ref _buffersProcessed, 0);
                _lastStatsUpdate = now;
            }
        }
#endif

        /// <summary>
        /// Optimized sample submission with pre-sized buffer for known input sizes
        /// </summary>
        /// <param name="samples">Input samples</param>
        /// <param name="sampleCount">Actual number of samples (may be less than array length)</param>
        public void SubmitSamplesOptimized(float[] samples, int sampleCount)
        {
            if (_disposed || samples == null || sampleCount <= 0)
                return;

            // Create a span for the actual data
            var samplesSpan = samples.AsSpan(0, sampleCount);
            SubmitSamplesSpan(samplesSpan);
        }

        /// <summary>
        /// High-performance span-based sample submission
        /// </summary>
        public void SubmitSamplesSpan(ReadOnlySpan<float> samples)
        {
            if (_disposed || samples.Length == 0)
                return;

            int inputFrames = samples.Length / InputDataChannels;
            int outputBufferSize = _framesPerBuffer * _engineChannels;

            EnsureConversionBufferAllocated(outputBufferSize);

            for (int i = 0; i + _framesPerBuffer <= inputFrames; i += _framesPerBuffer)
            {
                var processedBuffer = ProcessSampleSpanChunk(samples, i, outputBufferSize);
                if (processedBuffer != null)
                {
                    _pendingBuffers.Enqueue(processedBuffer);
                    _batchSignal.Set();
                }
            }
        }

        /// <summary>
        /// Process sample chunk from ReadOnlySpan
        /// </summary>
        private float[] ProcessSampleSpanChunk(ReadOnlySpan<float> samples, int startFrame, int outputBufferSize)
        {
            float[] buffer = AudioBufferPool.Rent(outputBufferSize);

            try
            {
                Array.Clear(buffer, 0, outputBufferSize);

                int inputStart = startFrame * InputDataChannels;
                var inputChunk = samples.Slice(inputStart, _framesPerBuffer * InputDataChannels);

                if (_isMonoToStereo)
                {
                    ConvertMonoToStereoSpan(inputChunk, buffer.AsSpan());
                }
                else if (_isStereoToMono)
                {
                    ConvertStereoToMonoSpan(inputChunk, buffer.AsSpan());
                }
                else if (_isSameFormat)
                {
                    inputChunk.CopyTo(buffer.AsSpan());
                }
                else
                {
                    AudioBufferPool.Return(buffer);
                    return null;
                }

                return buffer;
            }
            catch
            {
                AudioBufferPool.Return(buffer);
                return null;
            }
        }

        /// <summary>
        /// Span-based mono to stereo conversion
        /// </summary>
        private void ConvertMonoToStereoSpan(ReadOnlySpan<float> input, Span<float> output)
        {
            for (int i = 0; i < input.Length; i++)
            {
                float sample = input[i];
                output[i * 2] = sample;     // Left
                output[i * 2 + 1] = sample; // Right
            }
        }

        /// <summary>
        /// Span-based stereo to mono conversion
        /// </summary>
        private void ConvertStereoToMonoSpan(ReadOnlySpan<float> input, Span<float> output)
        {
            for (int i = 0; i < output.Length; i++)
            {
                float left = input[i * 2];
                float right = input[i * 2 + 1];
                output[i] = (left + right) * 0.5f;
            }
        }

        /// <summary>
        /// Seeks to the specified position in the audio source. No operation for real-time streams.
        /// </summary>
        /// <param name="position">The target playback position.</param>
        public void Seek(TimeSpan position)
        {
            // No-op for real-time stream
            // Could potentially clear pending buffers if needed
            if (_disposed) return;

            // Optional: Clear pending buffers on seek
            while (_pendingBuffers.TryDequeue(out var buffer))
            {
                AudioBufferPool.Return(buffer);
            }
        }

        /// <summary>
        /// Changes the playback state of the source to Idle, Playing, or Paused.
        /// </summary>
        /// <param name="state">The desired <see cref="SourceState"/>.</param>
        public void ChangeState(SourceState state)
        {
            if (_disposed)
                return;

            switch (state)
            {
                case SourceState.Idle:
                    Stop();
                    break;
                case SourceState.Playing:
                    Play();
                    break;
                case SourceState.Paused:
                    Pause();
                    break;
            }
        }

        /// <summary>
        /// Starts playback if not already playing.
        /// </summary>
        private void Play()
        {
            if (State == SourceState.Playing)
                return;

            SetAndRaiseStateChanged(SourceState.Playing);
            Logger?.LogInfo("SourceSound playback started");
        }

        /// <summary>
        /// Pauses playback if currently playing or buffering.
        /// </summary>
        private void Pause()
        {
            if (State == SourceState.Playing || State == SourceState.Buffering)
            {
                SetAndRaiseStateChanged(SourceState.Paused);
                Logger?.LogInfo("SourceSound playback paused");
            }
        }

        /// <summary>
        /// Stops playback, clears queued samples, and sets state to Idle.
        /// </summary>
        private void Stop()
        {
            if (State == SourceState.Idle)
                return;

            State = SourceState.Idle;

            // Clear all queued buffers and return to pool
            while (SourceSampleData.TryDequeue(out var buffer))
            {
                AudioBufferPool.Return(buffer);
            }

            while (_pendingBuffers.TryDequeue(out var buffer))
            {
                AudioBufferPool.Return(buffer);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            Logger?.LogInfo("SourceSound playback stopped");
        }

        /// <summary>
        /// Sets the internal state and raises the <see cref="StateChanged"/> event if the state changed.
        /// </summary>
        /// <param name="state">The new <see cref="SourceState"/> to apply.</param>
        private void SetAndRaiseStateChanged(SourceState state)
        {
            bool raise = State != state;
            State = state;
            if (raise)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Sets the internal playback position and raises the <see cref="PositionChanged"/> event if it changed.
        /// </summary>
        /// <param name="position">The new playback position.</param>
        private void SetAndRaisePositionChanged(TimeSpan position)
        {
            bool raise = Position != position;
            Position = position;
            if (raise)
                PositionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Processes the provided audio samples through custom and volume processors.
        /// </summary>
        /// <param name="samples">The span of audio samples to process.</param>
        private void ProcessSampleProcessors(Span<float> samples)
        {
            if (CustomSampleProcessor is { IsEnabled: true })
                CustomSampleProcessor.Process(samples);

            if (VolumeProcessor.Volume != 1.0f)
                VolumeProcessor.Process(samples);
        }

        /// <summary>
        /// Retrieves raw byte audio data for a given position. (Not implemented for real-time)
        /// </summary>
        public byte[] GetByteAudioData(TimeSpan position) => Array.Empty<byte>();

        /// <summary>
        /// Retrieves floating-point audio data for a given position. (Not implemented for real-time)
        /// </summary>
        public float[] GetFloatAudioData(TimeSpan position) => Array.Empty<float>();

        /// <summary>
        /// Disposes the source, stops playback, clears queued samples, and suppresses finalization.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            Logger?.LogInfo("Disposing SourceSound...");

            // Stop batch processing
            _batchProcessingActive = false;
            _batchSignal.Set();

            // Wait for batch processing thread to finish
            _batchProcessingThread?.Join(1000);

            State = SourceState.Idle;

            // Clear all buffers and return to pool
            while (SourceSampleData.TryDequeue(out var buffer))
            {
                AudioBufferPool.Return(buffer);
            }

            while (_pendingBuffers.TryDequeue(out var buffer))
            {
                AudioBufferPool.Return(buffer);
            }

            // Return conversion buffer
            lock (_conversionLock)
            {
                if (_conversionBuffer != null)
                {
                    AudioBufferPool.Return(_conversionBuffer);
                    _conversionBuffer = null;
                }
            }

            _batchSignal?.Dispose();

            GC.SuppressFinalize(this);
            _disposed = true;

            Logger?.LogInfo("SourceSound disposed successfully");
        }
    }
}
