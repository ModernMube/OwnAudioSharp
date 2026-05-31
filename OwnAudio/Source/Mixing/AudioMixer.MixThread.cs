using System.Runtime.CompilerServices;
using OwnaudioNET.Engine;
using OwnaudioNET.Synchronization;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Mix thread loop - continuously mixes sources and sends to engine.
    /// This is the hot path - must be zero-allocation.
    /// OPTIMIZATION: Uses cached array instead of ConcurrentDictionary.Values to avoid enumerator allocation.
    /// OPTIMIZATION: Parallel multi-threaded source processing.
    /// </summary>
    private void MixThreadLoop()
    {
        int bufferSizeInSamples = _bufferSizeInFrames * _config.Channels;
        float[] mixBuffer = new float[bufferSizeInSamples];
        float[] sourceBuffer = new float[bufferSizeInSamples];

        while (!_shouldStop)
        {
            try
            {
                if (!_isRunning)
                {
                    _pauseEvent.Wait(100);
                    continue;
                }

                if (_sourcesArrayNeedsUpdate)
                {
                    _cachedSourcesArray = _sources.Values.ToArray();
                    _sourcesArrayNeedsUpdate = false;
                }

                Array.Clear(mixBuffer, 0, bufferSizeInSamples);
                double currentTimestamp = _masterClock.CurrentTimestamp;
                
                int activeSources;
                bool dropoutDetected = false;

                if (_masterClock.Mode == ClockMode.Realtime)
                {
                    dropoutDetected = MixSourcesRealtime(mixBuffer, sourceBuffer, currentTimestamp, out activeSources);
                }
                else
                {
                    activeSources = MixSourcesOffline(mixBuffer, sourceBuffer, currentTimestamp);
                }

                if (activeSources > 0)
                {
                    ApplyMasterVolume(mixBuffer.AsSpan(0, bufferSizeInSamples));
                    ApplyMasterEffects(mixBuffer.AsSpan(0, bufferSizeInSamples), _bufferSizeInFrames);
                    ApplyLimiter(mixBuffer.AsSpan(0, bufferSizeInSamples));
                    CalculatePeakLevels(mixBuffer.AsSpan(0, bufferSizeInSamples));
                    
                    if (_isRecording)
                    {
                        WriteToRecorder(mixBuffer.AsSpan(0, bufferSizeInSamples));
                    }

                    SendToOutput(mixBuffer.AsSpan(0, bufferSizeInSamples));
                    
                    Interlocked.Add(ref _totalMixedFrames, _bufferSizeInFrames);

                    _masterClock.Advance(_bufferSizeInFrames);

                }
                else
                {
                    SendToOutput(mixBuffer.AsSpan(0, bufferSizeInSamples));
                    
                    _leftPeak = 0.0f;
                    _rightPeak = 0.0f;
                    
                    _masterClock.Advance(_bufferSizeInFrames);
                    
                    if (!_playbackEndedFired && _sources.Count > 0)
                    {
                        var snapshot = _cachedSourcesArray;
                        bool allEnded = snapshot.Length > 0 &&
                                        Array.TrueForAll(snapshot, s => s.State == AudioState.EndOfStream);
                        if (allEnded)
                        {
                            _playbackEndedFired = true;
                            ThreadPool.QueueUserWorkItem(_ => PlaybackEnded?.Invoke(this, EventArgs.Empty));
                        }
                    }
                    
                    Thread.Sleep(_mixIntervalMs * 2);
                }
            }
            catch (Exception)
            {
                Thread.Sleep(_mixIntervalMs * 2);
            }
        }
    }

    /// <summary>
    /// Sends the mixed buffer to the audio output.
    /// When an <see cref="AudioEngineWrapper"/> was provided at construction, output is routed
    /// through its internal <see cref="OwnaudioNET.BufferManagement.CircularBuffer"/> for pre-buffering.
    /// Otherwise, output goes directly to the underlying <see cref="IAudioEngine"/>.
    /// </summary>
    /// <param name="buffer">Interleaved float audio data to send.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SendToOutput(Span<float> buffer)
    {
        if (_engineWrapper != null)
            _engineWrapper.Send(buffer);
        else
            _engine.Send(buffer);
    }

    /// <summary>
    /// Mixes sources in realtime mode (non-blocking, dropout handling).
    /// NEW - v2.4.0+ Master Clock System
    /// Reverted to sequential processing for stability.
    /// </summary>
    /// <returns>True if any dropout was detected, false otherwise</returns>
    private bool MixSourcesRealtime(float[] mixBuffer, float[] sourceBuffer, double timestamp, out int activeSources)
    {
        activeSources = 0;
        bool dropoutDetected = false;

        var sources = _cachedSourcesArray;
        for (int i = 0; i < sources.Length; i++)
        {
            var source = sources[i];

            try
            {
                if (source.State != AudioState.Playing)
                    continue;

                if (source is IMasterClockSource clockSource && clockSource.IsAttachedToClock)
                {
                    bool success = clockSource.ReadSamplesAtTime(
                        timestamp,
                        sourceBuffer.AsSpan(),
                        _bufferSizeInFrames,
                        out ReadResult result);

                    if (result.FramesRead > 0)
                    {
                        if (source is BaseAudioSource bs && bs.OutputChannelMapping != null)
                        {
                            int sourceSampleCount = result.FramesRead * bs.Config.Channels;
                            MixIntoBufferSelective(
                                mixBuffer,
                                sourceBuffer,
                                sourceSampleCount,
                                bs.OutputChannelMapping,
                                _config.Channels);
                        }
                        else
                        {
                            MixIntoBuffer(mixBuffer, sourceBuffer, result.FramesRead * _config.Channels);
                        }
                    }

                    if (success)
                    {
                        activeSources++;
                    }
                    else
                    {
                        dropoutDetected = true;

                        OnTrackDropout(new TrackDropoutEventArgs(
                            source.Id,
                            source.GetType().Name,
                            timestamp,
                            _masterClock.CurrentSamplePosition,
                            _bufferSizeInFrames - result.FramesRead,
                            result.ErrorMessage ?? "Buffer underrun"));

                        lock (_metricsLock)
                        {
                            if (_trackMetrics.TryGetValue(source.Id, out var metrics))
                            {
                                metrics.RecordDropout(timestamp, _bufferSizeInFrames - result.FramesRead);
                            }
                        }
                    }
                }
                else
                {
                    int framesRead = source.ReadSamples(sourceBuffer, _bufferSizeInFrames);

                    if (framesRead > 0)
                    {
                        if (source is BaseAudioSource bs && bs.OutputChannelMapping != null)
                        {
                            int sourceSampleCount = framesRead * bs.Config.Channels;
                            MixIntoBufferSelective(
                                mixBuffer,
                                sourceBuffer,
                                sourceSampleCount,
                                bs.OutputChannelMapping,
                                _config.Channels);
                        }
                        else
                        {
                            MixIntoBuffer(mixBuffer, sourceBuffer, framesRead * _config.Channels);
                        }
                        activeSources++;
                    }
                }
            }
            catch (Exception ex)
            {
                OnSourceError(source, new AudioErrorEventArgs(
                    $"Error reading from source {source.Id}: {ex.Message}", ex));
            }
        }

        return dropoutDetected;
    }

    /// <summary>
    /// Mixes sources in offline mode (blocking, deterministic rendering).
    /// NEW - v2.4.0+ Master Clock System
    /// </summary>
    private int MixSourcesOffline(float[] mixBuffer, float[] sourceBuffer, double timestamp)
    {
        int activeSources = 0;

        for (int i = 0; i < _cachedSourcesArray.Length; i++)
        {
            var source = _cachedSourcesArray[i];

            try
            {
                if (source.State != AudioState.Playing)
                    continue;

                if (source is IMasterClockSource clockSource && clockSource.IsAttachedToClock)
                {
                    bool success = false;
                    ReadResult result = default;
                    int retryCount = 0;
                    int maxRetries = 5000; // 5 seconds timeout (1ms sleep per retry)

                    while (!success && retryCount < maxRetries)
                    {
                        success = clockSource.ReadSamplesAtTime(
                            timestamp,
                            sourceBuffer.AsSpan(),
                            _bufferSizeInFrames,
                            out result);

                        if (!success)
                        {
                            Thread.Sleep(1);
                            retryCount++;
                        }
                    }

                    if (success && result.FramesRead > 0)
                    {
                        if (source is BaseAudioSource bs && bs.OutputChannelMapping != null)
                        {
                            int sourceSampleCount = result.FramesRead * bs.Config.Channels;
                            MixIntoBufferSelective(
                                mixBuffer,
                                sourceBuffer,
                                sourceSampleCount,
                                bs.OutputChannelMapping,
                                _config.Channels);
                        }
                        else
                        {
                            MixIntoBuffer(mixBuffer, sourceBuffer, result.FramesRead * _config.Channels);
                        }
                        activeSources++;
                    }
                    else
                    {
                        OnSourceError(source, new AudioErrorEventArgs(
                            $"Offline rendering timeout for source {source.Id} at timestamp {timestamp:F3}s", null));
                    }
                }
                else
                {
                    int framesRead = source.ReadSamples(sourceBuffer, _bufferSizeInFrames);

                    if (framesRead > 0)
                    {
                        if (source is BaseAudioSource bs && bs.OutputChannelMapping != null)
                        {
                            int sourceSampleCount = framesRead * bs.Config.Channels;
                            MixIntoBufferSelective(
                                mixBuffer,
                                sourceBuffer,
                                sourceSampleCount,
                                bs.OutputChannelMapping,
                                _config.Channels);
                        }
                        else
                        {
                            MixIntoBuffer(mixBuffer, sourceBuffer, framesRead * _config.Channels);
                        }
                        activeSources++;
                    }
                }
            }
            catch (Exception ex)
            {
                OnSourceError(source, new AudioErrorEventArgs(
                    $"Error reading from source {source.Id} in offline mode: {ex.Message}", ex));
            }
        }

        return activeSources;
    }


    /// <summary>
    /// Fires the TrackDropout event (NEW - v2.4.0+).
    /// </summary>
    private void OnTrackDropout(TrackDropoutEventArgs e)
    {
        TrackDropout?.Invoke(this, e);
    }

    /// <summary>
    /// Resynchronizes sources to the master timestamp.
    /// </summary>
    /// <param name="masterTimestamp">Current MasterClock timestamp in seconds</param>
    private void ResyncSources(double masterTimestamp)
    {
        foreach (var source in _cachedSourcesArray)
        {
            try
            {
                if (source.State != AudioState.Playing)
                    continue;

                if (source is IMasterClockSource clockSource && clockSource.IsAttachedToClock)
                    continue;

                source.Seek(masterTimestamp);
            }
            catch (Exception ex)
            {
                OnSourceError(source, new AudioErrorEventArgs(
                    $"Error resyncing source {source.Id}: {ex.Message}", ex));
            }
        }
    }
}
