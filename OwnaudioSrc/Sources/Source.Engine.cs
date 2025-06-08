using Ownaudio.Decoders;
using Ownaudio.Sources.Extensions;
using Ownaudio.Utilities.Extensions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Decoders;
using Ownaudio.Engines;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Sources;

public partial class Source : ISource
{
    /// <summary>
    /// Handles audio decoder error, returns <c>true</c> to continue decoder thread, <c>false</c> will
    /// break the thread. By default, this will try to re-initializes <see cref="CurrentDecoder"/>
    /// and seeks to the last position.
    /// </summary>
    /// <param name="result">Failed audio decoder result.</param>
    /// <returns><c>true</c> will continue decoder thread, <c>false</c> will break the thread.</returns>
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
    /// It continuously decodes data during playback.
    /// </summary>
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

                IsEOF = true;
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
    /// Continuous processing and preparation of data for the output audio engine
    /// <see cref="SoundTouch"/>
    /// <see cref="CurrentDecoder"/>
    /// </summary>
    private void RunEngine()
    {
        Logger?.LogInfo("Engine thread is started.");
        List<float> soundTouchBuffer = new List<float>();
        double calculateTime = 0;
        int numSamples = 0;
        bool isSoundtouch = true;
        
        soundTouch.Clear();
        soundTouch.SampleRate = SourceManager.OutputEngineOptions.SampleRate;
        soundTouch.Channels = (int)SourceManager.OutputEngineOptions.Channels;

        List<float> soundTouchBuffer = new List<float>();

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

            if (soundTouchBuffer.Count >= FixedBufferSize)  
            {
                SendDataEngine(soundTouchBuffer, calculateTime);
            }
            else
            {
                if (Queue.TryDequeue(out var frame))
                {
                    Span<float> samples = MemoryMarshal.Cast<byte, float>(frame.Data);
                    calculateTime = frame.PresentationTime;
                    if(isSoundtouch)
                    {
                        lock (lockObject)
                        {
                            soundTouch.PutSamples(samples.ToArray(), samples.Length / soundTouch.Channels);

                            float[] processedSamples = new float[FixedBufferSize];
                            numSamples = soundTouch.ReceiveSamples(processedSamples, FixedBufferSize / soundTouch.Channels); //We request the processed data

                            numSamples = soundTouch.ReceiveSamples(_reusableProcessedSamples, FixedBufferSize / soundTouch.Channels);

                            if (numSamples > 0)
                            {
                                soundTouchBuffer.AddRange(processedSamples.Take(numSamples * soundTouch.Channels));
                            }
                        }
                    }
                    else
                    {
                        soundTouchBuffer.AddRange(samples.ToArray());
                    } 
                }
                else
                {
                    if (IsEOF)
                    {
                        numSamples = 0;
                        float[] processedSamples = new float[FixedBufferSize];
                        
                        if (Pitch != 0 || Tempo != 0)
                            numSamples = soundTouch.ReceiveSamples(processedSamples, FixedBufferSize / soundTouch.Channels); //If there is no more data from the decoder, empty the buffer of the SoundTouch

                        if (numSamples > 0)
                        {
                            do
                            {
                                soundTouchBuffer.AddRange(processedSamples.Take(numSamples * soundTouch.Channels));
                                SendDataEngine(soundTouchBuffer, calculateTime);

                                numSamples = soundTouch.ReceiveSamples(processedSamples, FixedBufferSize / soundTouch.Channels);
                            }while (numSamples > 0);

                            Logger?.LogInfo("End of audio data and SoundTouch buffer is empty.");
                            break;
                        }
                        else if (soundTouchBuffer.Count > 0)
                            SendDataEngine(soundTouchBuffer, calculateTime);

                        Logger?.LogInfo("End of audio data and SoundTouch buffer is empty."); //The SoundTouch buffer is also empty, let's end the playback
                        break;
                    }
                    else
                    {
                        Logger?.LogInfo("Waiting for more data from the decoder...");  //If we have not reached the end of the file, wait for the decoder to provide new data.
                        Thread.Sleep(10);
                        continue;
                    }
                }
            }
        }

        SetAndRaisePositionChanged(TimeSpan.Zero);

        // Just fire and forget, and it should be non-blocking event.
        Task.Run(() => SetAndRaiseStateChanged(SourceState.Idle)); 

        Logger?.LogInfo("Engine thread is completed.");
    }

    /// <summary>
    /// Prepares the data for the source manager
    /// </summary>
    /// <param name="soundTouchBuffer">List of processed data</param>
    /// <param name="calculateTime">Time calculated from the data</param>
#nullable disable
    private void SendDataEngine(List<float> soundTouchBuffer, double calculateTime)
    {
        int samplesSize = FramesPerBuffer * CurrentDecoder.StreamInfo.Channels;
        float[] samples;        

        for (int i = 0; i < (soundTouchBuffer.Count / samplesSize) ; i++)
        {
            samples = soundTouchBuffer.Take(samplesSize).ToArray();

            if (soundTouchBuffer.Count >= samplesSize)
                soundTouchBuffer.RemoveRange(0, samplesSize);

            ProcessSampleProcessors(samples);

            SourceSampleData.Enqueue(samples);

            if (CurrentDecoder is not null)
                calculateTime += (samples.Length / CurrentDecoder.StreamInfo.SampleRate * CurrentDecoder.StreamInfo.Channels) * 1000;

            SetAndRaisePositionChanged(TimeSpan.FromMilliseconds(calculateTime));
        }
    }

    /// <summary>
    /// Returns the contents of the audio file loaded into the source in a byte array.
    /// </summary>
    /// <param name="position">
    /// Jumps to the position specified in the parameter after decoding all the data. 
    /// The most typical is zero (the beginning of the file).
    /// </param>
    /// <returns>The array containing the data.</returns>
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
    /// Returns the contents of the audio file loaded into the source in a float array.
    /// </summary>
    /// <param name="position">
    /// Jumps to the position specified in the parameter after decoding all the data. 
    /// The most typical is zero (the beginning of the file).
    /// </param>
    /// <returns>The array containing the data.</returns>
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

    /// <summary>
    /// Releases all engine-related resources and returns rented arrays to the pool.
    /// </summary>
    /// <remarks>
    /// This method should be called during disposal to properly clean up 
    /// ArrayPool rented arrays and other engine resources.
    /// </remarks>
    private void DisposeEngineResources()
    {
        _reusableProcessedSamples?.ReturnToPool(_floatArrayPool);
        _reusableSamplesArray?.ReturnToPool(_floatArrayPool);
        _reusableSoundTouchBuffer.Clear();
    }
#nullable restore
}
