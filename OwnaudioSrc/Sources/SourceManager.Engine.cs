using Ownaudio.Engines;
using Ownaudio.Sources.Extensions;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Sources;

public partial class SourceManager
{
    /// <summary>
    /// Pre-allocated buffer for audio mixing operations to avoid frequent memory allocations.
    /// </summary>
    private float[]? _mixBuffer;

    /// <summary>
    /// Tracks the last allocated mix buffer size to determine when reallocation is needed.
    /// </summary>
    private int _lastMixBufferSize = 0;

    /// <summary>
    /// Pre-allocated buffer for level calculations to avoid Task allocations.
    /// </summary>
    private float[]? _levelCalculationBuffer;

    /// <summary>
    /// Reusable Task for level calculations to avoid Task allocations.
    /// </summary>
    private Task _levelCalculationTask = Task.CompletedTask;

    /// <summary>
    /// Reusable Task for file saving operations to avoid Task allocations.
    /// </summary>
    private Task _fileSaveTask = Task.CompletedTask;

    /// <summary>
    /// The main mixing engine that combines multiple audio sources into a single output stream.
    /// Runs continuously while the SourceManager is in an active state.
    /// </summary>
    /// <remarks>
    /// This method performs the core audio mixing operations:
    /// - Manages seeking operations and source synchronization
    /// - Mixes multiple output sources using scalar operations
    /// - Processes input recording and mixing
    /// - Applies sample processors (volume, custom effects)
    /// - Sends mixed audio to the output engine
    /// - Updates playback position based on processed samples
    /// - Handles end-of-stream conditions and playback reset
    /// - Writes recorded data to file when configured
    /// 
    /// The mixing process uses pre-allocated buffers for optimal performance
    /// and includes comprehensive synchronization logic for multi-source playback.
    /// </remarks>
    private void MixEngine()
    {
        bool useSources = false;
        TimeSpan lastKnownPosition = Position;
        bool seekJustHappened = false;

        int maxLength = CalculateMaxBufferLength();
        EnsureMixBufferAllocated(maxLength);
        EnsureLevelCalculationBufferAllocated(maxLength);

        double sampleDurationMs = 1000.0 / OutputEngineOptions.SampleRate;
        int channelCount = (int)OutputEngineOptions.Channels;

        while (State != SourceState.Idle)
        {
            if (IsSeeking)
            {
                seekJustHappened = true;
                Thread.Yield(); 
                FastClear(_mixBuffer, maxLength);
                continue;
            }

            if (seekJustHappened && !IsSeeking)
            {
                lastKnownPosition = Position;
                seekJustHappened = false;

                bool allSourcesReady = Sources.All(src => src.SourceSampleData.Count > 0 || src.State == SourceState.Idle);
                if (!allSourcesReady)
                {
                    Thread.Yield();
                    continue;
                }
            }

            FastClear(_mixBuffer, maxLength);

            if (!Sources.Any(p => p.SourceSampleData.Count() > 0))
            {
                if (Sources.All(p => p.State == SourceState.Idle))
                {
                    SetAndRaisePositionChanged(TimeSpan.Zero);
                }

                if (!IsRecorded)
                {
                    if (!Sources.Any(p => p.SourceSampleData.Count() > 0))
                    {
                        Engine?.Send(_mixBuffer.AsSpan(0, maxLength)); // Silence
                        Thread.Yield();
                        continue;
                    }
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
                    Thread.Sleep(10);
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

            if (maxLength > 0)
            {
                if (Sources.Count() > 0 && useSources && State == SourceState.Playing)
                {
                    ProcessSourceMixingSimple(lastKnownPosition);
                }

                if (IsRecorded && Engine is not null)
                {
                    ProcessInputMixingSimple();
                }

                ProcessSampleProcessors(_mixBuffer.AsSpan(0, maxLength));

                Engine?.Send(_mixBuffer.AsSpan(0, maxLength));

                if (State == SourceState.Playing)
                {
                    double processedTimeMs = (maxLength / channelCount) * sampleDurationMs;
                    lastKnownPosition = lastKnownPosition.Add(TimeSpan.FromMilliseconds(processedTimeMs));

                    if (Math.Abs((lastKnownPosition - Position).TotalMilliseconds) > 20)
                    {
                        SetAndRaisePositionChanged(lastKnownPosition);
                    }
                }

                if (Position.TotalMilliseconds >= Duration.TotalMilliseconds)
                {
                    SetAndRaisePositionChanged(Duration);
                    ResetPlayback();
                    break;
                }
            }
        }

        writeDataToFile();
    }

#nullable disable
    /// <summary>
    /// Processes and mixes multiple output sources with synchronization checking.
    /// </summary>
    /// <param name="lastKnownPosition">The last known playback position for synchronization purposes.</param>
    /// <remarks>
    /// This method performs the following operations:
    /// - Synchronization check for multiple sources (only when more than one source exists)
    /// - Detects time drift between sources and corrects by seeking
    /// - Mixes audio samples using simple scalar addition
    /// - Applies audio clamping to prevent clipping
    /// - Returns buffers to the pool for memory efficiency
    /// 
    /// The synchronization tolerance is set to 200ms for detection and 100ms for correction,
    /// providing a balance between accuracy and stability.
    /// </remarks>
    private void ProcessSourceMixingSimple(TimeSpan lastKnownPosition)
    {
        if (Sources.Count > 1)
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
                foreach (ISource src in Sources)
                {
                    if (Math.Abs((src.Position - lastKnownPosition).TotalMilliseconds) > 100)
                    {
                        src.Seek(lastKnownPosition);
                    }
                }
            }
        }

        foreach (ISource src in Sources)
        {
            if (src.SourceSampleData.TryDequeue(out float[] samples))
            {
                try
                {
                    int mixLength = Math.Min(samples.Length, _mixBuffer.Length);

                    for (int i = 0; i < mixLength; i++)
                    {
                        _mixBuffer[i] += samples[i];
                        _mixBuffer[i] = FastClamp(_mixBuffer[i]);
                    }
                }
                finally
                {
                    SimpleAudioBufferPool.Return(samples);
                }
            }
        }

        ProcessSparkSourcesMixing();
    }

    /// <summary>
    /// Processes the mixing of multiple spark audio sources, ensuring a cohesive output stream.
    /// Integrates real-time mixing adjustments and synchronizations.
    /// </summary>
    /// <remarks>
    /// This method is responsible for the following operations:
    /// - Managing individual spark source timing and alignment
    /// - Harmonizing multiple spark sources into a unified audio output
    /// - Applying dynamic adjustments, including gain and effects
    /// - Resolving potential conflicts or overlaps between spark sources
    /// - Ensuring minimal latency and real-time responsiveness during mixing
    /// - Supporting diverse audio formats with consistent results
    /// The method employs optimized mixing algorithms designed for scalability
    /// and low memory overhead, ensuring high performance across various audio conditions.
    /// </remarks>
    private void ProcessSparkSourcesMixing()
    {
        foreach (var sparkSource in SourcesSpark.ToList())
        {
            if (sparkSource.IsPlaying && sparkSource.SourceSampleData.TryDequeue(out float[] samples))
            {
                try
                {
                    int mixLength = Math.Min(samples.Length, _mixBuffer.Length);

                    for (int i = 0; i < mixLength; i++)
                    {
                        _mixBuffer[i] += samples[i];
                        _mixBuffer[i] = FastClamp(_mixBuffer[i]);
                    }

                    // Ha a simple source befejeződött és nem loopol, távolítsd el
                    if (sparkSource.HasFinished && !sparkSource.IsLooping)
                    {
                        for (int i = 0; i < inputBuffer.Length; i++)
                        {
                            _mixedBuffer[i * 2] += Math.Clamp(inputBuffer[i], -1.0f, 1.0f);     //Left channel
                            _mixedBuffer[i * 2 + 1] += inputBuffer[i]; //Right channel
                            _mixedBuffer[i * 2] = Math.Clamp(_mixedBuffer[i * 2], -1.0f, 1.0f);
                            _mixedBuffer[i * 2 + 1] = Math.Clamp(_mixedBuffer[i * 2 + 1], -1.0f, 1.0f);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < inputBuffer.Length; i++)
                        {
                            _mixedBuffer[i] += inputBuffer[i];
                            _mixedBuffer[i] = Math.Clamp(_mixedBuffer[i], -1.0f, 1.0f);
                        }
                    }

                    if(InputEngineOptions.Channels == OwnAudioEngine.EngineChannels.Stereo)
                        InputLevels = CalculateAverageStereoLevels(_mixedBuffer);
                    else
                        InputLevels = CalculateAverageMonoLevel(_mixedBuffer);
                }

                Span<float> mixedSpan = CollectionsMarshal.AsSpan(_mixedBuffer.ToList());

                ProcessSampleProcessors(mixedSpan);

                Engine?.Send(mixedSpan);

                if (State == SourceState.Playing)
                {
                    double processedTimeMs = (mixedBuffer.Count / channelCount) * sampleDurationMs;

                    lastKnownPosition = lastKnownPosition.Add(TimeSpan.FromMilliseconds(processedTimeMs));

                    if (Math.Abs((lastKnownPosition - Position).TotalMilliseconds) > 20)
                    {
                        SetAndRaisePositionChanged(lastKnownPosition);
                    }
                }

                mixedBuffer.Clear();

                if (Position.TotalMilliseconds >= Duration.TotalMilliseconds)
                {
                    SetAndRaisePositionChanged(Duration);
                    ResetPlayback();
                    break;
                }
            }
        }

        if (IsWriteData && File.Exists(writefilePath) && SaveWaveFileName is not null)
        {
            Task.Run(() =>
            {
                WriteWaveFile.WriteFile(
                    filePath: SaveWaveFileName,
                    rawFilePath: writefilePath,
                    sampleRate: OwnAudio.DefaultOutputDevice.DefaultSampleRate,
                    channels: 2,
                    bitPerSamples: BitPerSamples);
            });
            IsWriteData = false;
        }
    }

    /// <summary>
    /// Calculates the average signal level of a stereo audio signal for the left and right channels.
    /// </summary>
    /// <param name="stereoAudioData">Stereo audio data</param>
    /// <returns>Average data left and righ channel</returns>
    private (float, float) CalculateAverageStereoLevels(float[] stereoAudioData)
    {
        if (stereoAudioData == null || stereoAudioData.Length == 0)
        {
            Console.WriteLine("Nincs feldolgozandó adat.");
            return (0f, 0f);
        }

        // We use absolute values ​​because the signal level can be negative.
        float leftChannelSum = 0;
        float rightChannelSum = 0;
        int leftSampleCount = 0;
        int rightSampleCount = 0;

                mixedBuffer[frame * 2] = FastClamp(mixedBuffer[frame * 2]);
                mixedBuffer[frame * 2 + 1] = FastClamp(mixedBuffer[frame * 2 + 1]);
            }
        }
        else if (inputChannels == 2 && outputChannels == 1)
        {
            if (i % 2 == 0) // Left channel (even indices)
            {
                leftChannelSum += Math.Abs(stereoAudioData[i]);
                leftSampleCount++;
            }
            else // Right channel (odd indices)
            {
                mixedBuffer[i] += FastClamp(inputBuffer[i] * mixingGain);
                mixedBuffer[i] = FastClamp(mixedBuffer[i]);
            }
        }
        else
        {
            throw new NotSupportedException($"Mixing from {inputChannels} to {outputChannels} channels is not supported.");
        }
    }

    /// <summary>
    /// Calculates the average signal level of a mono audio signal.
    /// </summary>
    /// <param name="monoAudioData">Stereo audio data</param>
    /// <returns>Average data left and righ channel</returns>
    private (float, float) CalculateAverageMonoLevel(float[] monoAudioData)
    {
        if (monoAudioData == null || monoAudioData.Length == 0)
        {
            Console.WriteLine("Nincs feldolgozandó adat.");
            return (0f, 0f);
        }

        // We use absolute values ​​because the signal level can be negative.
        float leftChannelSum = 0;
        int leftSampleCount = 0;

        // Mono channel data
        for (int i = 0; i < monoAudioData.Length; i++)
        {
                leftChannelSum += Math.Abs(monoAudioData[i]);
        }

        // Calculating averages
        float leftAverage = leftSampleCount > 0 ? leftChannelSum / leftSampleCount : 0;

        return (leftAverage, 0f);
    }

    /// <summary>
    /// Resets the audio playback state after completion, preparing the system for reuse.
    /// </summary>
    /// <remarks>
    /// This method performs the following reset operations:
    /// - Sets the state to Idle and position to zero
    /// - Resets both output and input audio level meters
    /// - Clears all queued sample data from sources
    /// - Seeks all sources back to the beginning (time zero)
    /// 
    /// This reset allows the player to be reused for new playback sessions
    /// without requiring a complete system restart or reinitialization.
    /// </remarks>
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

        foreach (SourceSpark src in SourcesSpark)
        {
            while (src.SourceSampleData.TryDequeue(out _)) { }
            src.Seek(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Initializes the appropriate audio engine based on available audio backend systems.
    /// </summary>
    /// <returns>True if engine initialization was successful; otherwise, false.</returns>
    /// <remarks>
    /// This method handles engine initialization with the following priority:
    /// 1. PortAudio engine (if PortAudio is initialized)
    /// 2. MiniAudio engine (if MiniAudio is initialized and PortAudio is not)
    /// 
    /// For each engine type, it determines whether to initialize with:
    /// - Input + Output capabilities (if input device available and recording enabled)
    /// - Output-only capabilities (if no input needed)
    /// 
    /// The method automatically configures input options for MiniAudio to match
    /// output configuration when needed. Returns false if no suitable engine
    /// backend is available or initialization fails.
    /// </remarks>
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
    /// Fast audio clamping function that constrains values to the valid audio range [-1.0, 1.0].
    /// </summary>
    /// <param name="value">The audio sample value to clamp.</param>
    /// <returns>The clamped value within the range [-1.0, 1.0].</returns>
    /// <remarks>
    /// This method is aggressively inlined for maximum performance in audio processing loops.
    /// Audio clamping is essential to prevent:
    /// - Digital audio clipping and distortion
    /// - Hardware damage from excessive signal levels
    /// - Unwanted artifacts in the audio output
    /// 
    /// Values below -1.0 are clamped to -1.0, values above 1.0 are clamped to 1.0,
    /// and values within the valid range are passed through unchanged.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FastClamp(float value)
    {
        return value < -1.0f ? -1.0f : (value > 1.0f ? 1.0f : value);
    }

    /// <summary>
    /// Efficiently clears a float array buffer using size-optimized methods.
    /// </summary>
    /// <param name="buffer">The float array buffer to clear.</param>
    /// <param name="length">The number of elements to clear from the start of the buffer.</param>
    /// <remarks>
    /// This method uses size-based optimization for best performance:
    /// - For buffers ≤1024 elements: Uses Span.Clear() which is optimized for smaller buffers
    /// - For larger buffers: Uses Array.Clear() which is more efficient for larger memory blocks
    /// 
    /// This approach provides optimal clearing performance across different buffer sizes,
    /// which is important for real-time audio processing where clearing operations
    /// occur frequently in the mixing loop.
    /// </remarks>
    private static void FastClear(float[]? buffer, int length)
    {
        if (buffer == null) return;

        int clearLength = Math.Min(buffer.Length, length);

        if (clearLength <= 1024)
            buffer.AsSpan(0, clearLength).Clear();
        else
            Array.Clear(buffer, 0, clearLength);
    }

    /// <summary>
    /// Terminates and disposes the audio engine, freeing all associated resources.
    /// </summary>
    /// <remarks>
    /// This method safely shuts down the audio engine:
    /// - Checks if an engine instance exists
    /// - Properly disposes the engine to free audio resources
    /// - Sets the engine reference to null for garbage collection
    /// 
    /// This method should be called during system shutdown or when switching
    /// between different audio engine configurations to prevent resource leaks.
    /// </remarks>
    private void TerminateEngine()
    {
        if (Engine != null)
        {
            Engine.Dispose();
            Engine = null;
        }
    }
}
