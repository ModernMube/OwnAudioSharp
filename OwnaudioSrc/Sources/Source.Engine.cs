using Ownaudio.Decoders;
using Ownaudio.Engines;
using Ownaudio.Sources.Extensions;
using Ownaudio.Utilities.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Sources;

public partial class Source : ISource
{
    // Egyszerû pre-allocated buffer
    private float[]? _processedSamples;
    private int _lastProcessedSize = 0;

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
                CurrentDecoder = CurrentUrl != null ? CreateDecoder(CurrentUrl) : CreateDecoder(CurrentStream);
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

                if(!Queue.IsEmpty)
                    while (Queue.TryDequeue(out _)) { }
                
                if(!SourceSampleData.IsEmpty)
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
            lock(lockObject)
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

        double calculateTime = 0;
        int numSamples = 0;
        bool isSoundtouch = Pitch != 0 || Tempo != 0;

        // Ensure processed samples buffer
        EnsureProcessedSamplesBuffer();

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
                SendDataEngineSimple(soundTouchBuffer, calculateTime);
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
                            soundTouch.PutSamples(samples.ToArray(), samples.Length / soundTouch.Channels);

                            Array.Clear(_processedSamples, 0, FixedBufferSize);
                            numSamples = soundTouch.ReceiveSamples(_processedSamples, FixedBufferSize / soundTouch.Channels);

                            if (numSamples > 0)
                            {
                                soundTouchBuffer.AddRange(_processedSamples.Take(numSamples * soundTouch.Channels));
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
                        // Flush SoundTouch
                        if (isSoundtouch)
                        {
                            do
                            {
                                Array.Clear(_processedSamples, 0, FixedBufferSize);
                                numSamples = soundTouch.ReceiveSamples(_processedSamples, FixedBufferSize / soundTouch.Channels);

                                if (numSamples > 0)
                                {
                                    soundTouchBuffer.AddRange(_processedSamples.Take(numSamples * soundTouch.Channels));
                                    SendDataEngineSimple(soundTouchBuffer, calculateTime);
                                }
                            } while (numSamples > 0);
                        }
                        else if (soundTouchBuffer.Count > 0)
                        {
                            SendDataEngineSimple(soundTouchBuffer, calculateTime);
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
        Task.Run(() => SetAndRaiseStateChanged(SourceState.Idle));

        Logger?.LogInfo("Engine thread is completed.");
    }

    private void EnsureProcessedSamplesBuffer()
    {
        if (_processedSamples == null || _lastProcessedSize != FixedBufferSize)
        {
            _processedSamples = new float[FixedBufferSize];
            _lastProcessedSize = FixedBufferSize;
        }
    }

    /// <summary>
    /// Prepares the data for the source manager
    /// </summary>
    /// <param name="soundTouchBuffer">List of processed data</param>
    /// <param name="calculateTime">Time calculated from the data</param>
    private void SendDataEngineSimple(List<float> soundTouchBuffer, double calculateTime)
    {
        int samplesSize = FramesPerBuffer * CurrentDecoder.StreamInfo.Channels;

        for (int i = 0; i < (soundTouchBuffer.Count / samplesSize); i++)
        {
            float[] samples = samplesSize > 512
                ? SimpleAudioBufferPool.Rent(samplesSize)
                : new float[samplesSize];

            try
            {
                for (int j = 0; j < samplesSize && j < soundTouchBuffer.Count; j++)
                {
                    samples[j] = soundTouchBuffer[j];
                }

                if (soundTouchBuffer.Count >= samplesSize)
                    soundTouchBuffer.RemoveRange(0, samplesSize);

                ProcessSampleProcessors(samples.AsSpan(0, samplesSize));
                SourceSampleData.Enqueue(samples);

                if (CurrentDecoder is not null)
                    calculateTime += (samplesSize / (double)(CurrentDecoder.StreamInfo.SampleRate * CurrentDecoder.StreamInfo.Channels)) * 1000;

                SetAndRaisePositionChanged(TimeSpan.FromMilliseconds(calculateTime));

                samples = null;
            }
            finally
            {
                if (samples != null && samplesSize > 512)
                    SimpleAudioBufferPool.Return(samples);
            }
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
        if(IsLoaded)
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
#nullable restore
}
