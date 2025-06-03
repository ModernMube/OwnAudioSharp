using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Ownaudio.Engines;



namespace Ownaudio.Sources;

public unsafe partial class SourceManager
{
    /// <summary>
    /// Mix output sources into a stream. Considering the state of the source manager.
    /// </summary> 
    private void MixEngine()
    {
        bool useSources = false;
        List<float> mixedBuffer = new List<float>();
        List<float> _frequencies = new List<float>();

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

                if(!IsRecorded)
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

            if (mixedBuffer.Count > 0) 
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
                                needToResync = true;
                            }
                        }
                        
                        if (needToResync)
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

                if (IsRecorded && Engine is not null)
                {
                    float[] inputBuffer = new float[EngineFramesPerBuffer * (int)InputEngineOptions.Channels];
                    ((SourceInput)SourcesInput[0]).ReceivesData(out inputBuffer, Engine);

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

        writeDataToFile();
    }

    /// <summary>
    /// Optimized mixing function for different channel configurations
    /// </summary>
    /// <param name="inputBuffer">Input audio data</param>
    /// <param name="mixedBuffer">Output buffer (already contains data to be mixed into)</param>
    /// <param name="inputChannels">Number of input channels</param>
    /// <param name="outputChannels">Number of output channels</param>
    /// <param name="mixingGain">Mixing gain (0.0f - 1.0f)</param>
    public static void MixInput(ReadOnlySpan<float> inputBuffer, Span<float> mixedBuffer,
                               int inputChannels, int outputChannels, float mixingGain = 1.0f)
    {
        if (inputChannels == 1 && outputChannels == 2)
        {
            // Mono → Stereo
            int frames = inputBuffer.Length;
            for (int frame = 0; frame < frames; frame++)
            {
                float sample = Math.Clamp(inputBuffer[frame] * mixingGain, -1.0f, 1.0f);
                mixedBuffer[frame * 2] += sample;     // Left
                mixedBuffer[frame * 2 + 1] += sample; // Right
            }
        }
        else if (inputChannels == 2 && outputChannels == 1)
        {
            // Stereo → Mono
            int frames = inputBuffer.Length / 2;
            for (int frame = 0; frame < frames; frame++)
            {
                float left = inputBuffer[frame * 2] * mixingGain;
                float right = inputBuffer[frame * 2 + 1] * mixingGain;
                float monoSample = Math.Clamp((left + right) * 0.5f, -1.0f, 1.0f);
                mixedBuffer[frame] += monoSample;
            }
        }
        else if (inputChannels == outputChannels)
        {
            // Identical channels
            for (int i = 0; i < inputBuffer.Length; i++)
            {
                mixedBuffer[i] += Math.Clamp(inputBuffer[i] * mixingGain, -1.0f, 1.0f);
            }
        }
        else
        {
            throw new NotSupportedException($"Mixing from {inputChannels} to {outputChannels} channels is not supported.");
        }

        // Final clamp
        for (int i = 0; i < mixedBuffer.Length; i++)
        {
            mixedBuffer[i] = Math.Clamp(mixedBuffer[i], -1.0f, 1.0f);
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
