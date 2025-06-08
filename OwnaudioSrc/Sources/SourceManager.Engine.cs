using Ownaudio.Engines;
using Ownaudio.Sources.Extensions;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Sources;

public unsafe partial class SourceManager
{
    /// <summary>
    /// Mix output sources into a stream. Considering the state of the source manager.
    /// </summary> 
    private void MixEngine()
    {
        bool useSources = false;
        TimeSpan lastKnownPosition = Position;
        bool seekJustHappened = false;

        int maxLength = Sources.Max(src => 
            src.SourceSampleData.TryPeek(out float[]? peekedSamples) && peekedSamples != null 
            ? peekedSamples.Length 
            : 0);
                
        if (maxLength > 0)
            mixedBuffer.Capacity = maxLength;

        double sampleDurationMs = 1000.0 / OutputEngineOptions.SampleRate;
        int channelCount = (int)OutputEngineOptions.Channels;

        while (State != SourceState.Idle)
        {
            if (IsSeeking)
            {
                seekJustHappened = true;

                Thread.Sleep(1);
                mixedBuffer.Clear();
                continue;
            }

            if (seekJustHappened && !IsSeeking)
            {
                lastKnownPosition = Position;
                seekJustHappened = false;

                bool allSourcesReady = Sources.All(src => src.SourceSampleData.Count > 0 || src.State == SourceState.Idle);
                if (!allSourcesReady)
                {
                    Thread.Sleep(5);
                }
            }

            for (int i = 0; i < maxLength; i++)
                mixedBuffer.Add(0.0f);

            if (!Sources.Any(p => p.SourceSampleData.Count() > 0))
            {
                if (Sources.All(p => p.State == SourceState.Idle))
                {
                    SetAndRaisePositionChanged(TimeSpan.Zero);
                }

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
                float[] _mixedBuffer = new float[mixedBuffer.Count];
                
                for (int i = 0; i < maxLength; i++)
                    _mixedBuffer[i] = 0.0f;

                if (Sources.Count() > 0 && useSources)
                {
                    if (State == SourceState.Playing)
                    {
                        bool needToResync = false;

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
                        if (src.SourceSampleData.TryDequeue(out float[]? samples))
                        {
                            for (int i = 0; i < samples.Length; i++)
                            {
                                _mixedBuffer[i] += samples[i];
                                _mixedBuffer[i] = Math.Clamp(_mixedBuffer[i], -1.0f, 1.0f);
                            }
                        }
                    }
                }

    /// <summary>
    /// Processes input audio recording and mixes it with the output stream.
    /// </summary>
    /// <remarks>
    /// This method handles input audio processing:
    /// - Receives audio data from the input source
    /// - Performs channel configuration mixing (mono to stereo, stereo to mono, etc.)
    /// - Calculates input audio levels for monitoring
    /// - Applies appropriate mixing gain (0.8f default)
    /// - Returns input buffers to the pool for memory efficiency
    /// 
    /// The method supports both stereo and mono input configurations and automatically
    /// calculates appropriate audio levels for real-time monitoring.
    /// </remarks>
    private void ProcessInputMixingSimple()
    {
        if (SourcesInput.Count > 0)
        {
            int inputBufferSize = EngineFramesPerBuffer * (int)InputEngineOptions.Channels;

                    MixInput(
                        inputBuffer: inputBuffer,
                        mixedBuffer: _mixedBuffer,
                        inputChannels: (int)InputEngineOptions.Channels,
                        outputChannels: (int)OutputEngineOptions.Channels,
                        mixingGain: 0.8f
                    );

                    InputLevels = InputEngineOptions.Channels == OwnAudioEngine.EngineChannels.Stereo
                        ? CalculateAverageStereoLevels(inputBuffer)
                        : CalculateAverageMonoLevel(inputBuffer);
                }

                Span<float> mixedSpan = CollectionsMarshal.AsSpan(_mixedBuffer.ToList());

                ProcessSampleProcessors(mixedSpan);

                Engine?.Send(mixedSpan);

                if (State == SourceState.Playing)
                {
                    double processedTimeMs = (mixedBuffer.Count / channelCount) * sampleDurationMs;

    /// <summary>
    /// Ensures the mix buffer is allocated with the correct size.
    /// </summary>
    /// <param name="size">The required buffer size in samples.</param>
    /// <remarks>
    /// This method implements lazy buffer allocation:
    /// - Only allocates when the buffer is null or size has changed
    /// - Tracks the last allocated size to avoid unnecessary reallocations
    /// - Minimizes memory allocations during mixing operations
    /// 
    /// This approach provides optimal performance by avoiding frequent allocations
    /// while ensuring the buffer is always the correct size.
    /// </remarks>
    private void EnsureMixBufferAllocated(int size)
    {
        if (_mixBuffer == null || _lastMixBufferSize != size)
        {
            _mixBuffer = new float[size];
            _lastMixBufferSize = size;
        }
    }

    /// <summary>
    /// Calculates the maximum buffer length needed for mixing operations.
    /// </summary>
    /// <returns>The maximum buffer length in samples based on available sources and engine configuration.</returns>
    /// <remarks>
    /// This method determines the optimal buffer size by:
    /// - Checking all sources for their current buffer sizes
    /// - Using the engine's frames per buffer as a fallback
    /// - Accounting for channel configuration in the calculation
    /// 
    /// The buffer size affects both memory usage and latency, so this method
    /// ensures an appropriate balance based on current source requirements.
    /// </remarks>
    private int CalculateMaxBufferLength()
    {
        if (Sources.Count == 0)
            return EngineFramesPerBuffer * (int)OutputEngineOptions.Channels;

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
    /// Optimized mixing function for different channel configurations.
    /// </summary>
    /// <param name="inputBuffer">The input audio data to be mixed.</param>
    /// <param name="mixedBuffer">The output buffer that already contains data to be mixed into.</param>
    /// <param name="inputChannels">The number of input channels (1 for mono, 2 for stereo).</param>
    /// <param name="outputChannels">The number of output channels (1 for mono, 2 for stereo).</param>
    /// <param name="mixingGain">The mixing gain factor (0.0f to 1.0f, default: 1.0f).</param>
    /// <exception cref="NotSupportedException">Thrown when the input/output channel combination is not supported.</exception>
    /// <remarks>
    /// This method supports the following channel conversions:
    /// - Mono to Stereo: Duplicates mono signal to both left and right channels
    /// - Stereo to Mono: Averages left and right channels into a single mono signal
    /// - Same channels: Direct mixing without conversion
    /// 
    /// All mixing operations include audio clamping to prevent clipping and maintain
    /// signal integrity. The mixing gain allows for volume control during the mixing process.
    /// </remarks>
    public static void MixInputSimple(ReadOnlySpan<float> inputBuffer, Span<float> mixedBuffer,
                                     int inputChannels, int outputChannels, float mixingGain = 1.0f)
    {
        if (inputChannels == 1 && outputChannels == 2)
        {
            int frames = inputBuffer.Length;
            for (int frame = 0; frame < frames && frame * 2 + 1 < mixedBuffer.Length; frame++)
            {
                float sample = Math.Clamp(inputBuffer[frame] * mixingGain, -1.0f, 1.0f);
                mixedBuffer[frame * 2] += sample;     // Left
                mixedBuffer[frame * 2 + 1] += sample; // Right
            }
        }
        else if (inputChannels == 2 && outputChannels == 1)
        {
            int frames = inputBuffer.Length / 2;
            for (int frame = 0; frame < frames && frame < mixedBuffer.Length; frame++)
            {
                float left = inputBuffer[frame * 2] * mixingGain;
                float right = inputBuffer[frame * 2 + 1] * mixingGain;
                float monoSample = FastClamp((left + right) * 0.5f);

                mixedBuffer[frame] += monoSample;
                mixedBuffer[frame] = FastClamp(mixedBuffer[frame]);
            }
        }
        else if (inputChannels == outputChannels)
        {
            // Identical channels
            for (int i = 0; i < inputBuffer.Length; i++)
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
    /// Applies audio processing to the specified samples using volume and custom sample processors.
    /// Also handles audio level calculation and file recording operations.
    /// </summary>
    /// <param name="samples">The audio samples to process.</param>
    /// <remarks>
    /// This method performs the following operations in order:
    /// 1. Applies custom sample processor if enabled
    /// 2. Applies volume processor if volume is not at 100%
    /// 3. Calculates output audio levels asynchronously for monitoring
    /// 4. Writes sample data to file if recording is enabled
    /// 
    /// Audio level calculation and file writing operations are performed asynchronously
    /// to minimize impact on real-time audio processing performance.
    /// </remarks>
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

        if (IsWriteData)
        {
            var samplesArray = samples.ToArray();
            Task.Run(() => { SaveSamplesToFile(samplesArray, writefilePath); });
        }
    }

    /// <summary>
    /// Calculates the average signal levels for a stereo audio signal.
    /// </summary>
    /// <param name="stereoAudioData">The stereo audio data array where even indices are left channel and odd indices are right channel.</param>
    /// <returns>A tuple containing the average levels for (left channel, right channel).</returns>
    /// <remarks>
    /// This method processes stereo audio data by:
    /// - Separating left channel (even indices: 0, 2, 4, ...) and right channel (odd indices: 1, 3, 5, ...)
    /// - Using absolute values to measure signal amplitude regardless of polarity
    /// - Calculating separate averages for each channel
    /// - Returning (0, 0) if no data is available for processing
    /// 
    /// The returned values represent the average amplitude levels which can be used
    /// for audio level monitoring, VU meters, or automatic gain control.
    /// </remarks>
    private (float, float) CalculateAverageStereoLevels(float[] stereoAudioData)
    {
        if (stereoAudioData == null || stereoAudioData.Length == 0)
        {
            Console.WriteLine("No data available for processing.");
            return (0f, 0f);
        }

        // We use absolute values ​​because the signal level can be negative.
        float leftChannelSum = 0;
        float rightChannelSum = 0;
        int leftSampleCount = 0;
        int rightSampleCount = 0;

        for (int i = 0; i < stereoAudioData.Length; i++)
        {
            if (i % 2 == 0)
            {
                leftChannelSum += Math.Abs(stereoAudioData[i]);
                leftSampleCount++;
            }
            else
            {
                rightChannelSum += Math.Abs(stereoAudioData[i]);
                rightSampleCount++;
            }
        }

        float leftAverage = leftSampleCount > 0 ? leftChannelSum / leftSampleCount : 0;
        float rightAverage = rightSampleCount > 0 ? rightChannelSum / rightSampleCount : 0;

        return (leftAverage, rightAverage);
    }

    /// <summary>
    /// Calculates the average signal level for a mono audio signal.
    /// </summary>
    /// <param name="monoAudioData">The mono audio data array.</param>
    /// <returns>A tuple where the first value is the mono level and the second value is always 0 (for consistency with stereo format).</returns>
    /// <remarks>
    /// This method processes mono audio data by:
    /// - Using absolute values to measure signal amplitude regardless of polarity
    /// - Calculating the average amplitude across all samples
    /// - Returning the result in stereo-compatible format (mono level, 0)
    /// - Handling empty or null data gracefully
    /// 
    /// The returned format maintains consistency with stereo level calculations
    /// while providing meaningful mono audio level information.
    /// </remarks>
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
        if (OwnAudio.IsPortAudioInitialized && Engine is null)  // Portaudio engine initialze
        {
            if (OwnAudio.DefaultInputDevice.MaxInputChannels > 0 && IsRecorded)
                Engine = new OwnAudioEngine(InputEngineOptions, OutputEngineOptions, EngineFramesPerBuffer);
            else
                Engine = new OwnAudioEngine(OutputEngineOptions, EngineFramesPerBuffer);
        }
        else if (!OwnAudio.IsPortAudioInitialized && OwnAudio.IsMiniAudioInitialized && Engine is null)
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
    /// We will kill the audio engine
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