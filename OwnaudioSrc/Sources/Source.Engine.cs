using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Decoders;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Sources;

public partial class Source : ISource
{
    /// <summary>
    /// Shared array pool for float arrays to reduce GC pressure.
    /// </summary>
    private readonly ArrayPool<float> _floatArrayPool = ArrayPool<float>.Shared;

    /// <summary>
    /// Reusable buffer for SoundTouch processing to avoid repeated allocations.
    /// </summary>
    private readonly List<float> _reusableSoundTouchBuffer = new List<float>();

    /// <summary>
    /// Reusable array for processed samples from SoundTouch.
    /// </summary>
    private float[]? _reusableProcessedSamples;

    /// <summary>
    /// Reusable array for sample data processing.
    /// </summary>
    private float[]? _reusableSamplesArray;

    /// <summary>
    /// Handles audio decoder error and attempts recovery.
    /// </summary>
    /// <param name="result">The failed audio decoder result containing error information.</param>
    /// <returns>
    /// <c>true</c> to continue the decoder thread after recovery attempt; 
    /// <c>false</c> to break the thread.
    /// </returns>
    /// <remarks>
    /// By default, this method tries to re-initialize the current decoder 
    /// and seeks to the last known position.
    /// </remarks>
    protected virtual bool HandleDecoderError(AudioDecoderResult result)
    {
        while (Queue.TryDequeue(out _)) { }
        while (SourceSampleData.TryDequeue(out _)) { }
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
    /// Continuously decodes audio data during playback in a separate thread.
    /// </summary>
    /// <remarks>
    /// This method runs in a loop until the source state becomes idle, 
    /// decoding frames and managing the audio queue.
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
    /// Continuously processes and prepares audio data for the output engine using SoundTouch.
    /// </summary>
    /// <remarks>
    /// This method runs in a separate thread and handles audio processing, 
    /// including pitch and tempo adjustments through SoundTouch library.
    /// </remarks>
    private void RunEngine()
    {
        Logger?.LogInfo("Engine thread is started.");

        _reusableSoundTouchBuffer.Clear();
        double calculateTime = 0;
        int numSamples = 0;
        bool isSoundtouch = true;

        soundTouch.Clear();
        soundTouch.SampleRate = SourceManager.OutputEngineOptions.SampleRate;
        soundTouch.Channels = (int)SourceManager.OutputEngineOptions.Channels;

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

            if (_reusableSoundTouchBuffer.Count >= FixedBufferSize)
            {
                SendDataEngine(_reusableSoundTouchBuffer, calculateTime);
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

                            if (_reusableProcessedSamples == null || _reusableProcessedSamples.Length < FixedBufferSize)
                            {
                                _reusableProcessedSamples?.ReturnToPool(_floatArrayPool);
                                _reusableProcessedSamples = _floatArrayPool.Rent(FixedBufferSize);
                            }

                            numSamples = soundTouch.ReceiveSamples(_reusableProcessedSamples, FixedBufferSize / soundTouch.Channels);

                            if (numSamples > 0)
                            {
                                int actualSampleCount = numSamples * soundTouch.Channels;
                                for (int i = 0; i < actualSampleCount; i++)
                                {
                                    _reusableSoundTouchBuffer.Add(_reusableProcessedSamples[i]);
                                }
                            }
                        }
                    }
                    else
                    {
                        int currentCount = _reusableSoundTouchBuffer.Count;
                        _reusableSoundTouchBuffer.Capacity = Math.Max(_reusableSoundTouchBuffer.Capacity, currentCount + samples.Length);

                        for (int i = 0; i < samples.Length; i++)
                        {
                            _reusableSoundTouchBuffer.Add(samples[i]);
                        }
                    }
                }
                else
                {
                    if (IsEOF)
                    {
                        numSamples = 0;

                        if (_reusableProcessedSamples == null || _reusableProcessedSamples.Length < FixedBufferSize)
                        {
                            _reusableProcessedSamples?.ReturnToPool(_floatArrayPool);
                            _reusableProcessedSamples = _floatArrayPool.Rent(FixedBufferSize);
                        }

                        if (Pitch != 0 || Tempo != 0)
                            numSamples = soundTouch.ReceiveSamples(_reusableProcessedSamples, FixedBufferSize / soundTouch.Channels);

                        if (numSamples > 0)
                        {
                            do
                            {
                                int actualSampleCount = numSamples * soundTouch.Channels;
                                for (int i = 0; i < actualSampleCount; i++)
                                {
                                    _reusableSoundTouchBuffer.Add(_reusableProcessedSamples[i]);
                                }

                                SendDataEngine(_reusableSoundTouchBuffer, calculateTime);

                                numSamples = soundTouch.ReceiveSamples(_reusableProcessedSamples, FixedBufferSize / soundTouch.Channels);
                            } while (numSamples > 0);

                            Logger?.LogInfo("End of audio data and SoundTouch buffer is empty.");
                            break;
                        }
                        else if (_reusableSoundTouchBuffer.Count > 0)
                            SendDataEngine(_reusableSoundTouchBuffer, calculateTime);

                        Logger?.LogInfo("End of audio data and SoundTouch buffer is empty.");
                        break;
                    }
                    else
                    {
                        Logger?.LogInfo("Waiting for more data from the decoder...");
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

    /// <summary>
    /// Prepares processed audio data for the source manager by chunking it into appropriate buffer sizes.
    /// </summary>
    /// <param name="soundTouchBuffer">The list containing processed audio data.</param>
    /// <param name="calculateTime">The presentation time calculated from the audio data.</param>
    /// <remarks>
    /// This method processes the audio data in chunks based on FramesPerBuffer size 
    /// and updates the position accordingly.
    /// </remarks>
#nullable disable
    private void SendDataEngine(List<float> soundTouchBuffer, double calculateTime)
    {
        int samplesSize = FramesPerBuffer * CurrentDecoder.StreamInfo.Channels;

        int completeChunks = soundTouchBuffer.Count / samplesSize;

        for (int i = 0; i < completeChunks; i++)
        {
            if (_reusableSamplesArray == null || _reusableSamplesArray.Length < samplesSize)
            {
                _reusableSamplesArray?.ReturnToPool(_floatArrayPool);
                _reusableSamplesArray = _floatArrayPool.Rent(samplesSize);
            }

            for (int j = 0; j < samplesSize; j++)
            {
                _reusableSamplesArray[j] = soundTouchBuffer[j];
            }

            if (soundTouchBuffer.Count >= samplesSize)
            {
                soundTouchBuffer.RemoveRange(0, samplesSize);
            }

            var samplesSpan = _reusableSamplesArray.AsSpan(0, samplesSize);
            ProcessSampleProcessors(samplesSpan);

            float[] enqueueSamples = _floatArrayPool.Rent(samplesSize);
            samplesSpan.CopyTo(enqueueSamples);

            SourceSampleData.Enqueue(enqueueSamples.AsSpan(0, samplesSize).ToArray());

            if (CurrentDecoder is not null)
                calculateTime += (samplesSize / CurrentDecoder.StreamInfo.SampleRate * CurrentDecoder.StreamInfo.Channels) * 1000;

            SetAndRaisePositionChanged(TimeSpan.FromMilliseconds(calculateTime));

            _floatArrayPool.Return(enqueueSamples);
        }
    }

    /// <summary>
    /// Returns the complete audio file content as a byte array.
    /// </summary>
    /// <param name="position">
    /// The position to seek to before decoding all data. 
    /// Typically set to zero to start from the beginning of the file.
    /// </param>
    /// <returns>
    /// A byte array containing the decoded audio data, or <c>null</c> if the source is not loaded.
    /// </returns>
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
    /// Returns the complete audio file content as a float array.
    /// </summary>
    /// <param name="position">
    /// The position to seek to before decoding all data. 
    /// Typically set to zero to start from the beginning of the file.
    /// </param>
    /// <returns>
    /// A float array containing the decoded audio samples, or <c>null</c> if the source is not loaded.
    /// </returns>
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

/// <summary>
/// Extension methods for cleaner ArrayPool usage.
/// </summary>
public static class ArrayPoolExtensions
{
    /// <summary>
    /// Returns an array to the specified ArrayPool if it is not null.
    /// </summary>
    /// <typeparam name="T">The type of elements in the array.</typeparam>
    /// <param name="array">The array to return to the pool.</param>
    /// <param name="pool">The ArrayPool to return the array to.</param>
    public static void ReturnToPool<T>(this T[] array, ArrayPool<T> pool)
    {
        if (array != null)
        {
            pool.Return(array, clearArray: false);
        }
    }
}
