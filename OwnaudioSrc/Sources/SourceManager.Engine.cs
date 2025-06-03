using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Engines;
using Ownaudio.Sources.Extensions;

namespace Ownaudio.Sources;

public unsafe partial class SourceManager
{
    /// <summary>
    /// Pre-allocated reusable buffer for mixed audio output to avoid allocation overhead on every frame.
    /// This buffer is resized as needed and helps improve performance by reducing garbage collection pressure.
    /// </summary>
    private float[]? _reusableMixedBuffer;

    /// <summary>
    /// Pre-allocated reusable buffer for input audio data to avoid allocation overhead during recording.
    /// This buffer is used when processing microphone or line input data.
    /// </summary>
    private float[]? _reusableInputBuffer;

    /// <summary>
    /// Tracks the last maximum buffer length to determine when buffer reallocation is necessary.
    /// Used to optimize memory management by avoiding unnecessary buffer recreations.
    /// </summary>
    private int _lastMaxLength = 0;

    /// <summary>
    /// Manual reset event for thread synchronization, signaling when audio buffers are ready for processing.
    /// Used to coordinate between audio processing threads and the main mixing engine.
    /// </summary>
    private readonly ManualResetEventSlim _bufferReady = new(false);

    /// <summary>
    /// Object used for thread synchronization during audio mixing operations.
    /// Ensures thread-safe access to shared audio buffers and prevents race conditions.
    /// </summary>
    private readonly object _mixingLock = new object();

    /// <summary>
    /// Counter that tracks the number of processed audio frames for performance monitoring.
    /// Used in debug builds to calculate and display mixing engine frame rate statistics.
    /// </summary>
    private int _frameCounter = 0;

    /// <summary>
    /// Timestamp of the last performance statistics update for monitoring mixing engine performance.
    /// Used to periodically log frame rate and buffer pool efficiency metrics in debug builds.
    /// </summary>
    private DateTime _lastStatsUpdate = DateTime.UtcNow;

    /// <summary>
    /// Processes audio samples through the volume processor and custom sample processor pipeline.
    /// Also handles output level calculation and optional file writing for recording functionality.
    /// </summary>
    /// <param name="samples">The audio samples to be processed. Modified in-place during processing.</param>
    protected virtual void ProcessSampleProcessors(Span<float> samples)
    {
        bool useCustomProcessor = CustomSampleProcessor is { IsEnabled: true };
        bool useVolumeProcessor = VolumeProcessor.Volume != 1.0f;

        if (useCustomProcessor || useVolumeProcessor)
        {
            if (useCustomProcessor && CustomSampleProcessor is not null)
                CustomSampleProcessor.Process(samples);

            if (useVolumeProcessor)
                VolumeProcessor.Process(samples);
        }

        lock (_lock)
        {
            float[] samplesArray = samples.ToArray();
            Task.Run(() =>
            {
                if (OutputEngineOptions.Channels == OwnAudioEngine.EngineChannels.Stereo)
                    OutputLevels = CalculateAverageStereoLevels(samplesArray);
                else
                    OutputLevels = CalculateAverageMonoLevel(samplesArray);
            });

        }

        if (IsWriteData) //Save data to file
        {
            var samplesArray = samples.ToArray();
            Task.Run(() => { SaveSamplesToFile(samplesArray, writefilePath); });
        }
    }

    /// <summary>
    /// Main audio mixing engine that runs in a separate thread.
    /// Handles real-time mixing of multiple audio sources, input recording, and playback state management.
    /// Uses optimized buffering and SIMD operations for high-performance audio processing.
    /// </summary>
    private void MixEngine()
    {
        bool useSources = false;
        TimeSpan lastKnownPosition = Position;
        bool seekJustHappened = false;

        // Calculate max buffer size once at the beginning
        int maxLength = CalculateMaxBufferLength();
        EnsureBuffersAllocated(maxLength);

        double sampleDurationMs = 1000.0 / OutputEngineOptions.SampleRate;
        int channelCount = (int)OutputEngineOptions.Channels;

        while (State != SourceState.Idle)
        {
            if (IsSeeking)
            {
                seekJustHappened = true;
                Thread.Yield(); // Yield instead of Sleep for better responsiveness
                continue;
            }

            if (seekJustHappened && !IsSeeking)
            {
                lastKnownPosition = Position;
                seekJustHappened = false;

                // Non-blocking check for source readiness
                bool allSourcesReady = CheckSourcesReady();
                if (!allSourcesReady)
                {
                    Thread.Yield();
                    continue;
                }
            }

            // Clear the reusable buffer using SIMD
            var mixedSpan = _reusableMixedBuffer.AsSpan(0, maxLength);
            SimdMixingHelper.ClearBufferSimd(mixedSpan);

            // Check if we have any audio data to process
            bool hasAudioData = HasAudioDataAvailable();

            if (!hasAudioData)
            {
                if (Sources.All(p => p.State == SourceState.Idle))
                {
                    SetAndRaisePositionChanged(TimeSpan.Zero);
                }

                if (!IsRecorded)
                {
                    Thread.Yield();
                    continue;
                }
                else
                {
                    useSources = false;
                }
            }

            if (State == SourceState.Paused)
            {
                if (!IsRecorded)
                {
                    Thread.Yield();
                    continue;
                }
                else
                {
                    useSources = false;
                }
            }
            else
            {
                useSources = true;
            }

            // Process audio mixing
            if (maxLength > 0)
            {
                ProcessAudioMixing(mixedSpan, useSources, ref lastKnownPosition);

                // Calculate and update position
                if (State == SourceState.Playing)
                {
                    UpdatePlaybackPosition(ref lastKnownPosition, maxLength, channelCount, sampleDurationMs);
                }

                // Check for end of playback
                if (Position.TotalMilliseconds >= Duration.TotalMilliseconds)
                {
                    SetAndRaisePositionChanged(Duration);
                    ResetPlayback();
                    break;
                }
            }

            // Performance monitoring (only in debug mode)
#if DEBUG
            UpdatePerformanceStats();
#endif
        }

        writeDataToFile();
    }

    /// <summary>
    /// Processes the complete audio mixing pipeline including source mixing, input recording, 
    /// sample processing, and output to the audio engine.
    /// </summary>
    /// <param name="mixedSpan">The buffer span where mixed audio will be written.</param>
    /// <param name="useSources">Whether to include audio sources in the mix.</param>
    /// <param name="lastKnownPosition">Reference to the last known playback position for tracking.</param>
    private void ProcessAudioMixing(Span<float> mixedSpan, bool useSources, ref TimeSpan lastKnownPosition)
    {
        // Source mixing
        if (Sources.Count > 0 && useSources && State == SourceState.Playing)
        {
            ProcessSourceMixing(mixedSpan, ref lastKnownPosition);
        }

        // Input recording mixing
        if (IsRecorded && Engine is not null)
        {
            ProcessInputMixing(mixedSpan);
        }

        // Apply sample processors
        ProcessSampleProcessors(mixedSpan);

        // Send to audio engine
        Engine?.Send(mixedSpan);
    }

    /// <summary>
    /// Processes mixing of multiple audio sources into the main output buffer.
    /// Handles source synchronization and uses SIMD-optimized mixing for performance.
    /// </summary>
    /// <param name="mixedSpan">The output buffer where source audio will be mixed.</param>
    /// <param name="lastKnownPosition">Reference to the last known playback position for synchronization.</param>
    private void ProcessSourceMixing(Span<float> mixedSpan, ref TimeSpan lastKnownPosition)
    {
        // Resync check for multiple sources
        if (Sources.Count > 1)
        {
            CheckAndResyncSources(lastKnownPosition);
        }

        // Mix all sources using optimized method
        foreach (ISource src in Sources)
        {
            if (src.SourceSampleData.TryDequeue(out float[]? samples))
            {
                try
                {
                    // SIMD optimized mixing
                    var samplesSpan = samples.AsSpan();
                    int mixLength = Math.Min(samplesSpan.Length, mixedSpan.Length);

                    SimdMixingHelper.MixBuffersSimd(
                        samplesSpan.Slice(0, mixLength),
                        mixedSpan.Slice(0, mixLength)
                    );
                }
                finally
                {
                    // Return buffer to pool
                    AudioBufferPool.Return(samples);
                }
            }
        }
    }

    /// <summary>
    /// Processes mixing of input recording data (microphone/line input) into the main output buffer.
    /// Handles channel conversion and applies appropriate mixing gain to prevent clipping.
    /// </summary>
    /// <param name="mixedSpan">The output buffer where input audio will be mixed.</param>
    private void ProcessInputMixing(Span<float> mixedSpan)
    {
        if (SourcesInput.Count > 0)
        {
            int inputBufferSize = EngineFramesPerBuffer * (int)InputEngineOptions.Channels;

            // Reuse pre-allocated buffer
            EnsureInputBufferAllocated(inputBufferSize);

            if (Engine != null)
            {
                ((SourceInput)SourcesInput[0]).ReceivesData(out var inputBuffer, Engine);

                try
                {
                    MixInput(
                        inputBuffer: inputBuffer.AsSpan(),
                        mixedBuffer: mixedSpan,
                        inputChannels: (int)InputEngineOptions.Channels,
                        outputChannels: (int)OutputEngineOptions.Channels,
                        mixingGain: 0.8f
                    );

                    // Calculate input levels
                    InputLevels = InputEngineOptions.Channels == OwnAudioEngine.EngineChannels.Stereo
                        ? CalculateAverageStereoLevels(inputBuffer)
                        : CalculateAverageMonoLevel(inputBuffer);
                }
                finally
                {
                    // Return input buffer to pool if it came from pool
                    if (inputBuffer != _reusableInputBuffer)
                    {
                        AudioBufferPool.Return(inputBuffer);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks and resynchronizes multiple audio sources to prevent drift and maintain synchronized playback.
    /// Uses parallel processing for efficient resynchronization when sources are out of sync.
    /// </summary>
    /// <param name="lastKnownPosition">The target position to synchronize all sources to.</param>
    private void CheckAndResyncSources(TimeSpan lastKnownPosition)
    {
        TimeSpan minPos = TimeSpan.MaxValue;
        TimeSpan maxPos = TimeSpan.MinValue;

        foreach (ISource src in Sources)
        {
            if (src.Position < minPos) minPos = src.Position;
            if (src.Position > maxPos) maxPos = src.Position;
        }

        if ((maxPos - minPos).TotalMilliseconds > 200)
        {
            // Parallel resync for better performance
            Parallel.ForEach(Sources, src =>
            {
                if (Math.Abs((src.Position - lastKnownPosition).TotalMilliseconds) > 100)
                {
                    src.Seek(lastKnownPosition);
                }
            });
        }
    }

    /// <summary>
    /// Updates the playback position based on the number of processed audio samples.
    /// Calculates the time advancement and triggers position change events when necessary.
    /// </summary>
    /// <param name="lastKnownPosition">Reference to the current playback position to be updated.</param>
    /// <param name="bufferLength">The number of samples processed in this frame.</param>
    /// <param name="channelCount">The number of audio channels for time calculation.</param>
    /// <param name="sampleDurationMs">The duration of a single sample in milliseconds.</param>
    private void UpdatePlaybackPosition(ref TimeSpan lastKnownPosition, int bufferLength, int channelCount, double sampleDurationMs)
    {
        double processedTimeMs = (bufferLength / channelCount) * sampleDurationMs;
        lastKnownPosition = lastKnownPosition.Add(TimeSpan.FromMilliseconds(processedTimeMs));

        if (Math.Abs((lastKnownPosition - Position).TotalMilliseconds) > 20)
        {
            SetAndRaisePositionChanged(lastKnownPosition);
        }
    }

    /// <summary>
    /// Calculates the maximum buffer length needed based on available audio sources.
    /// Returns a default size if no sources are available or if source data is not ready.
    /// </summary>
    /// <returns>The maximum buffer length in samples required for mixing operations.</returns>
    private int CalculateMaxBufferLength()
    {
        if (Sources.Count == 0) return EngineFramesPerBuffer * (int)OutputEngineOptions.Channels;

        int maxLength = 0;
        foreach (var src in Sources)
        {
            if (src.SourceSampleData.TryPeek(out float[]? peekedSamples) && peekedSamples != null)
            {
                maxLength = Math.Max(maxLength, peekedSamples.Length);
            }
        }

        return maxLength > 0 ? maxLength : EngineFramesPerBuffer * (int)OutputEngineOptions.Channels;
    }

    /// <summary>
    /// Ensures that the main mixing buffer is allocated with the required size.
    /// Reallocates the buffer only when necessary to optimize memory usage.
    /// </summary>
    /// <param name="maxLength">The required buffer size in samples.</param>
    private void EnsureBuffersAllocated(int maxLength)
    {
        if (_reusableMixedBuffer == null || _lastMaxLength != maxLength)
        {
            _reusableMixedBuffer = new float[maxLength];
            _lastMaxLength = maxLength;
        }
    }

    /// <summary>
    /// Ensures that the input buffer is allocated with sufficient size for recording operations.
    /// Reallocates the buffer only when a larger size is needed.
    /// </summary>
    /// <param name="size">The required input buffer size in samples.</param>
    private void EnsureInputBufferAllocated(int size)
    {
        if (_reusableInputBuffer == null || _reusableInputBuffer.Length < size)
        {
            _reusableInputBuffer = new float[size];
        }
    }

    /// <summary>
    /// Checks if all audio sources are ready for processing by verifying they have sample data available.
    /// Returns true if all sources have data queued or are in idle state.
    /// </summary>
    /// <returns>True if all sources are ready for processing; otherwise, false.</returns>
    private bool CheckSourcesReady()
    {
        return Sources.All(src => src.SourceSampleData.Count > 0 || src.State == SourceState.Idle);
    }

    /// <summary>
    /// Checks if any audio sources have data available for processing.
    /// Used to determine if the mixing engine should continue processing or wait.
    /// </summary>
    /// <returns>True if at least one source has audio data available; otherwise, false.</returns>
    private bool HasAudioDataAvailable()
    {
        return Sources.Any(p => p.SourceSampleData.Count > 0);
    }

#if DEBUG
    /// <summary>
    /// Updates performance statistics for monitoring mixing engine efficiency.
    /// Logs frame rate and buffer pool statistics periodically in debug builds.
    /// This method is only compiled in debug configurations.
    /// </summary>
    private void UpdatePerformanceStats()
    {
        _frameCounter++;
        var now = DateTime.UtcNow;
        if ((now - _lastStatsUpdate).TotalSeconds >= 5.0)
        {
            double fps = _frameCounter / (now - _lastStatsUpdate).TotalSeconds;
            Logger?.LogInfo($"Mix Engine FPS: {fps:F1}, Buffer Pool Stats: {GetBufferPoolStats()}");

            _frameCounter = 0;
            _lastStatsUpdate = now;
        }
    }

    /// <summary>
    /// Retrieves buffer pool statistics for performance monitoring and debugging.
    /// Returns a string containing information about buffer pool efficiency and usage.
    /// This method is only compiled in debug configurations.
    /// </summary>
    /// <returns>A string containing buffer pool performance statistics.</returns>
    private string GetBufferPoolStats()
    {
        // Buffer pool statistics for debugging
        return "Pool efficiency monitoring";
    }
#endif

    /// <summary>
    /// Optimized mixing function with SIMD support for high-performance audio channel mixing.
    /// Handles various channel configurations including mono-to-stereo and stereo-to-mono conversion.
    /// </summary>
    /// <param name="inputBuffer">The input audio buffer to be mixed.</param>
    /// <param name="mixedBuffer">The output buffer where mixed audio will be written.</param>
    /// <param name="inputChannels">The number of input audio channels.</param>
    /// <param name="outputChannels">The number of output audio channels.</param>
    /// <param name="mixingGain">The gain factor applied during mixing to control volume. Default is 1.0f.</param>
    /// <exception cref="NotSupportedException">Thrown when the channel configuration is not supported.</exception>
    public static void MixInput(ReadOnlySpan<float> inputBuffer, Span<float> mixedBuffer,
                               int inputChannels, int outputChannels, float mixingGain = 1.0f)
    {
        if (inputChannels == 1 && outputChannels == 2)
        {
            // Mono → Stereo with SIMD optimization
            MixMonoToStereoSimd(inputBuffer, mixedBuffer, mixingGain);
        }
        else if (inputChannels == 2 && outputChannels == 1)
        {
            // Stereo → Mono
            MixStereoToMonoSimd(inputBuffer, mixedBuffer, mixingGain);
        }
        else if (inputChannels == outputChannels)
        {
            // Identical channels - direct SIMD mixing
            var scaledInput = stackalloc float[inputBuffer.Length];
            var scaledSpan = new Span<float>(scaledInput, inputBuffer.Length);

            // Scale input with SIMD
            for (int i = 0; i < inputBuffer.Length; i++)
            {
                scaledSpan[i] = inputBuffer[i] * mixingGain;
            }

            SimdMixingHelper.MixBuffersSimd(scaledSpan, mixedBuffer);
        }
        else
        {
            throw new NotSupportedException($"Mixing from {inputChannels} to {outputChannels} channels is not supported.");
        }
    }

    /// <summary>
    /// Converts mono audio input to stereo output using SIMD optimization.
    /// Duplicates the mono signal to both left and right channels with gain control and clipping prevention.
    /// </summary>
    /// <param name="input">The mono input audio buffer.</param>
    /// <param name="output">The stereo output buffer where converted audio will be written.</param>
    /// <param name="gain">The gain factor applied to the input signal.</param>
    private static void MixMonoToStereoSimd(ReadOnlySpan<float> input, Span<float> output, float gain)
    {
        int frames = input.Length;
        for (int frame = 0; frame < frames && frame * 2 + 1 < output.Length; frame++)
        {
            float sample = Math.Clamp(input[frame] * gain, -1.0f, 1.0f);
            output[frame * 2] += sample;     // Left
            output[frame * 2 + 1] += sample; // Right

            // Clamp output
            output[frame * 2] = Math.Clamp(output[frame * 2], -1.0f, 1.0f);
            output[frame * 2 + 1] = Math.Clamp(output[frame * 2 + 1], -1.0f, 1.0f);
        }
    }

    /// <summary>
    /// Converts stereo audio input to mono output using SIMD optimization.
    /// Averages the left and right channels with gain control and clipping prevention.
    /// </summary>
    /// <param name="input">The stereo input audio buffer.</param>
    /// <param name="output">The mono output buffer where converted audio will be written.</param>
    /// <param name="gain">The gain factor applied to the input signal.</param>
    private static void MixStereoToMonoSimd(ReadOnlySpan<float> input, Span<float> output, float gain)
    {
        int frames = input.Length / 2;
        for (int frame = 0; frame < frames && frame < output.Length; frame++)
        {
            float left = input[frame * 2] * gain;
            float right = input[frame * 2 + 1] * gain;
            float monoSample = Math.Clamp((left + right) * 0.5f, -1.0f, 1.0f);

            output[frame] += monoSample;
            output[frame] = Math.Clamp(output[frame], -1.0f, 1.0f);
        }
    }

    /// <summary>
    /// Clears all audio buffer pools to free memory and reset pool state.
    /// This method should be called during cleanup or when resetting the audio system.
    /// </summary>
    public void ClearBufferPools()
    {
        AudioBufferPool.Clear();
    }

    /// <summary>
    /// Calculates the average signal level of a stereo audio signal for both left and right channels.
    /// Uses absolute values to measure signal amplitude regardless of polarity.
    /// </summary>
    /// <param name="stereoAudioData">The stereo audio data array where even indices are left channel and odd indices are right channel.</param>
    /// <returns>A tuple containing the average levels for left and right channels respectively.</returns>
    private (float, float) CalculateAverageStereoLevels(float[] stereoAudioData)
    {
        if (stereoAudioData == null || stereoAudioData.Length == 0)
        {
            Console.WriteLine("Nincs feldolgozandó adat.");
            return (0f, 0f);
        }

        // We use absolute values because the signal level can be negative.
        float leftChannelSum = 0;
        float rightChannelSum = 0;
        int leftSampleCount = 0;
        int rightSampleCount = 0;

        // Left channel: 0, 2, 4, ...
        // Right channel: 1, 3, 5, ...
        for (int i = 0; i < stereoAudioData.Length; i++)
        {
            if (i % 2 == 0) // Left channel (even indices)
            {
                leftChannelSum += Math.Abs(stereoAudioData[i]);
                leftSampleCount++;
            }
            else // Right channel (odd indices)
            {
                rightChannelSum += Math.Abs(stereoAudioData[i]);
                rightSampleCount++;
            }
        }

        // Calculating averages
        float leftAverage = leftSampleCount > 0 ? leftChannelSum / leftSampleCount : 0;
        float rightAverage = rightSampleCount > 0 ? rightChannelSum / rightSampleCount : 0;

        return (leftAverage, rightAverage);
    }

    /// <summary>
    /// Calculates the average signal level of a mono audio signal.
    /// Returns the result as a tuple format for consistency with stereo level calculation.
    /// </summary>
    /// <param name="monoAudioData">The mono audio data array to analyze.</param>
    /// <returns>A tuple where the first value is the average mono level and the second value is always 0 (no right channel).</returns>
    private (float, float) CalculateAverageMonoLevel(float[] monoAudioData)
    {
        if (monoAudioData == null || monoAudioData.Length == 0)
        {
            return (0f, 0f);
        }

        float leftChannelSum = 0;

        for (int i = 0; i < monoAudioData.Length; i++)
        {
            leftChannelSum += Math.Abs(monoAudioData[i]);
        }

        float leftAverage = monoAudioData.Length > 0 ? leftChannelSum / monoAudioData.Length : 0;
        return (leftAverage, 0f);
    }

    /// <summary>
    /// Resets the player state after playback is finished to prepare for the next playback session.
    /// Clears all source buffers, resets positions and levels, without completely stopping the engine.
    /// </summary>
    private void ResetPlayback()
    {
        SetAndRaiseStateChanged(SourceState.Idle);
        SetAndRaisePositionChanged(TimeSpan.Zero);

        OutputLevels = (0f, 0f);
        InputLevels = (0f, 0f);

        foreach (ISource src in Sources)
        {
            while (src.SourceSampleData.TryDequeue(out _)) { }
            src.Seek(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Initializes the audio engine based on current configuration settings.
    /// Supports both PortAudio and MiniAudio backends with input/output capability detection.
    /// </summary>
    /// <returns>True if the audio engine was successfully initialized; otherwise, false.</returns>
    private bool InitializeEngine()
    {
        if (OwnAudio.IsPortAudioInitialized && Engine is null)  // Portaudio engine initialize
        {
            if (OwnAudio.DefaultInputDevice.MaxInputChannels > 0 && IsRecorded)
                Engine = new OwnAudioEngine(InputEngineOptions, OutputEngineOptions, EngineFramesPerBuffer);
            else
                Engine = new OwnAudioEngine(OutputEngineOptions, EngineFramesPerBuffer);
        }
        else if (!OwnAudio.IsPortAudioInitialized && OwnAudio.IsMiniAudioInitialized && Engine is null)  // Miniaudio engine initialize
        {
            if (OwnAudio.DefaultInputDevice.MaxInputChannels > 0 && IsRecorded)
            {
                InputEngineOptions = new AudioEngineInputOptions(OutputEngineOptions.Channels, OutputEngineOptions.SampleRate);
                Engine = new OwnAudioMiniEngine(InputEngineOptions, OutputEngineOptions, EngineFramesPerBuffer);
            }
            else
                Engine = new OwnAudioMiniEngine(OutputEngineOptions, EngineFramesPerBuffer);
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Terminates and disposes the current audio engine instance.
    /// Properly cleans up audio engine resources and sets the engine reference to null.
    /// </summary>
    private void TerminateEngine()
    {
        if (Engine != null)
        {
            Engine.Dispose();

            Engine = null;
        }
    }
}
