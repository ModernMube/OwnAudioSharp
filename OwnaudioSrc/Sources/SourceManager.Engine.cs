using Ownaudio.Engines;
using Ownaudio.Sources.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Sources;

public unsafe partial class SourceManager
{
    // Egyetlen pre-allocated buffer a mixing-hez
    private float[] _mixBuffer;
    private int _lastMixBufferSize = 0;

    /// <summary>
    /// Mix output sources into a stream. Considering the state of the source manager.
    /// </summary>  
    private void MixEngine()
    {
        bool useSources = false;
        TimeSpan lastKnownPosition = Position;
        bool seekJustHappened = false;

        // Egy buffer allokálás a kezdeten
        int maxLength = CalculateMaxBufferLength();
        EnsureMixBufferAllocated(maxLength);

        double sampleDurationMs = 1000.0 / OutputEngineOptions.SampleRate;
        int channelCount = (int)OutputEngineOptions.Channels;

        while (State != SourceState.Idle)
        {
            if (IsSeeking)
            {
                seekJustHappened = true;
                Thread.Sleep(1); // Eredeti sleep - egyszerű és működik
                Array.Clear(_mixBuffer, 0, maxLength);
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
                    continue;
                }
            }

            // Clear mix buffer - egyszerű, gyors
            Array.Clear(_mixBuffer, 0, maxLength);

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
                // Source mixing - egyszerű scalar művelet
                if (Sources.Count() > 0 && useSources && State == SourceState.Playing)
                {
                    ProcessSourceMixingSimple(lastKnownPosition);
                }

                // Input recording mixing
                if (IsRecorded && Engine is not null)
                {
                    ProcessInputMixingSimple();
                }

                // Apply sample processors
                ProcessSampleProcessors(_mixBuffer.AsSpan(0, maxLength));

                // Send to audio engine
                Engine?.Send(_mixBuffer.AsSpan(0, maxLength));

                // Update position
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

    private void ProcessSourceMixingSimple(TimeSpan lastKnownPosition)
    {
        // Sync check csak ha több source van
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

        // Egyszerű mixing - scalar, de gyors
        foreach (ISource src in Sources)
        {
            if (src.SourceSampleData.TryDequeue(out float[]? samples))
            {
                try
                {
                    int mixLength = Math.Min(samples.Length, _mixBuffer.Length);

                    // Egyszerű scalar mixing
                    for (int i = 0; i < mixLength; i++)
                    {
                        _mixBuffer[i] += samples[i];
                        _mixBuffer[i] = Math.Clamp(_mixBuffer[i], -1.0f, 1.0f);
                    }
                }
                finally
                {
                    // Pool visszaadás csak nagy buffer-eknél
                    SimpleAudioBufferPool.Return(samples);
                }
            }
        }
    }

    private void ProcessInputMixingSimple()
    {
        if (SourcesInput.Count > 0)
        {
            int inputBufferSize = EngineFramesPerBuffer * (int)InputEngineOptions.Channels;

            ((SourceInput)SourcesInput[0]).ReceivesData(out var inputBuffer, Engine);

            try
            {
                // Egyszerű input mixing
                MixInputSimple(
                    inputBuffer: inputBuffer.AsSpan(),
                    mixedBuffer: _mixBuffer.AsSpan(),
                    inputChannels: (int)InputEngineOptions.Channels,
                    outputChannels: (int)OutputEngineOptions.Channels,
                    mixingGain: 0.8f
                );

                InputLevels = InputEngineOptions.Channels == OwnAudioEngine.EngineChannels.Stereo
                    ? CalculateAverageStereoLevels(inputBuffer)
                    : CalculateAverageMonoLevel(inputBuffer);
            }
            finally
            {
                // Input buffer-t is pool-ba ha érdemes
                SimpleAudioBufferPool.Return(inputBuffer);
            }
        }
    }

    private void EnsureMixBufferAllocated(int size)
    {
        if (_mixBuffer == null || _lastMixBufferSize != size)
        {
            _mixBuffer = new float[size];
            _lastMixBufferSize = size;
        }
    }

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
    /// Optimized mixing function for different channel configurations
    /// </summary>
    /// <param name="inputBuffer">Input audio data</param>
    /// <param name="mixedBuffer">Output buffer (already contains data to be mixed into)</param>
    /// <param name="inputChannels">Number of input channels</param>
    /// <param name="outputChannels">Number of output channels</param>
    /// <param name="mixingGain">Mixing gain (0.0f - 1.0f)</param>
    public static void MixInputSimple(ReadOnlySpan<float> inputBuffer, Span<float> mixedBuffer,
                                     int inputChannels, int outputChannels, float mixingGain = 1.0f)
    {
        if (inputChannels == 1 && outputChannels == 2)
        {
            // Mono → Stereo
            int frames = inputBuffer.Length;
            for (int frame = 0; frame < frames && frame * 2 + 1 < mixedBuffer.Length; frame++)
            {
                float sample = Math.Clamp(inputBuffer[frame] * mixingGain, -1.0f, 1.0f);
                mixedBuffer[frame * 2] += sample;     // Left
                mixedBuffer[frame * 2 + 1] += sample; // Right

                mixedBuffer[frame * 2] = Math.Clamp(mixedBuffer[frame * 2], -1.0f, 1.0f);
                mixedBuffer[frame * 2 + 1] = Math.Clamp(mixedBuffer[frame * 2 + 1], -1.0f, 1.0f);
            }
        }
        else if (inputChannels == 2 && outputChannels == 1)
        {
            // Stereo → Mono
            int frames = inputBuffer.Length / 2;
            for (int frame = 0; frame < frames && frame < mixedBuffer.Length; frame++)
            {
                float left = inputBuffer[frame * 2] * mixingGain;
                float right = inputBuffer[frame * 2 + 1] * mixingGain;
                float monoSample = Math.Clamp((left + right) * 0.5f, -1.0f, 1.0f);

                mixedBuffer[frame] += monoSample;
                mixedBuffer[frame] = Math.Clamp(mixedBuffer[frame], -1.0f, 1.0f);
            }
        }
        else if (inputChannels == outputChannels)
        {
            // Identical channels
            int length = Math.Min(inputBuffer.Length, mixedBuffer.Length);
            for (int i = 0; i < length; i++)
            {
                mixedBuffer[i] += Math.Clamp(inputBuffer[i] * mixingGain, -1.0f, 1.0f);
                mixedBuffer[i] = Math.Clamp(mixedBuffer[i], -1.0f, 1.0f);
            }
        }
        else
        {
            throw new NotSupportedException($"Mixing from {inputChannels} to {outputChannels} channels is not supported.");
        }
    }

    /// <summary>
    /// Run <see cref="VolumeProcessor"/> and <see cref="CustomSampleProcessor"/> to the specified samples.
    /// </summary>
    /// <param name="samples">Audio samples to process to.</param>
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
    /// </summary>
    /// <param name="monoAudioData">Stereo audio data</param>
    /// <returns>Average data left and righ channel</returns>
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
    /// Resets the player after playback is finished,
    /// so that it can be used again without completely stopping it.
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
    /// Let's initialize the audio engine based on the settings. Returns true on successful initialization
    /// </summary>
    /// <returns></returns>
    private bool InitializeEngine()
    {
        if (OwnAudio.IsPortAudioInitialized && Engine is null)  // Portaudio engine initialze
        {
            if (OwnAudio.DefaultInputDevice.MaxInputChannels > 0 && IsRecorded)
                Engine = new OwnAudioEngine(InputEngineOptions, OutputEngineOptions, EngineFramesPerBuffer);
            else
                Engine = new OwnAudioEngine(OutputEngineOptions, EngineFramesPerBuffer);
        }
        else if(!OwnAudio.IsPortAudioInitialized && OwnAudio.IsMiniAudioInitialized && Engine is null)  // Miniaudio engine initialize
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
    private void TerminateEngine()
    {
        if (Engine != null)
        {
            Engine.Dispose();

            Engine = null;
        }
    }
}
