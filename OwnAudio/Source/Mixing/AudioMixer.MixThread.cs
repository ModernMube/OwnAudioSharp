using Ownaudio.Core;
using Ownaudio.Synchronization;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Mix thread loop - continuously mixes sources and sends to engine.
    /// This is the hot path - must be zero-allocation.
    /// OPTIMIZATION: Uses cached array instead of ConcurrentDictionary.Values to avoid enumerator allocation.
    /// </summary>
    private void MixThreadLoop()
    {
        // Pre-allocate buffers (done once, outside loop)
        int bufferSizeInSamples = _bufferSizeInFrames * _config.Channels;
        float[] mixBuffer = new float[bufferSizeInSamples];
        float[] sourceBuffer = new float[bufferSizeInSamples];

        while (!_shouldStop)
        {
            try
            {
                // Wait if paused
                if (!_isRunning)
                {
                    _pauseEvent.Wait(100);
                    continue;
                }

                // OPTIMIZATION: Update cached sources array if needed (zero allocation in steady state)
                // This avoids ConcurrentDictionary.Values enumeration which allocates an enumerator every call
                if (_sourcesArrayNeedsUpdate)
                {
                    _cachedSourcesArray = _sources.Values.ToArray();
                    _sourcesArrayNeedsUpdate = false;
                }

                // Clear mix buffer
                Array.Clear(mixBuffer, 0, bufferSizeInSamples);

                // 1. Get current timestamp from Master Clock
                double currentTimestamp = _masterClock.CurrentTimestamp;

                // 2. Mix sources based on rendering mode
                int activeSources;
                if (_masterClock.Mode == ClockMode.Realtime)
                {
                    // Realtime mode: Non-blocking, dropouts → silence + event
                    activeSources = MixSourcesRealtime(mixBuffer, sourceBuffer, currentTimestamp);
                }
                else
                {
                    // Offline mode: Blocking, deterministic
                    activeSources = MixSourcesOffline(mixBuffer, sourceBuffer, currentTimestamp);
                }

                // 3-7. Process mixed audio
                if (activeSources > 0)
                {
                    // 3. Apply master volume
                    ApplyMasterVolume(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // 4. Apply master effects
                    ApplyMasterEffects(mixBuffer.AsSpan(0, bufferSizeInSamples), _bufferSizeInFrames);

                    // 5. Calculate peak levels
                    CalculatePeakLevels(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // 6. Write to recorder if active
                    if (_isRecording)
                    {
                        WriteToRecorder(mixBuffer.AsSpan(0, bufferSizeInSamples));
                    }

                    // 7. Send to engine (BLOCKING CALL - provides natural timing)
                    _engine.Send(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // Update statistics
                    Interlocked.Add(ref _totalMixedFrames, _bufferSizeInFrames);

                    // 8. Advance Master Clock
                    _masterClock.Advance(_bufferSizeInFrames);

                    // LEGACY: Advance GhostTracks if any exist (deprecated path)
                    AdvanceLegacyGhostTracks(sourceBuffer);
                }
                else
                {
                    // No active sources - send silence
                    _engine.Send(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // Reset peak levels
                    _leftPeak = 0.0f;
                    _rightPeak = 0.0f;

                    // Still advance clock even with silence (timeline keeps moving)
                    _masterClock.Advance(_bufferSizeInFrames);

                    // Sleep longer when no sources are active
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
    /// Mixes sources in realtime mode (non-blocking, dropout handling).
    /// NEW - v2.4.0+ Master Clock System
    /// </summary>
    private int MixSourcesRealtime(float[] mixBuffer, float[] sourceBuffer, double timestamp)
    {
        int activeSources = 0;

        for (int i = 0; i < _cachedSourcesArray.Length; i++)
        {
            var source = _cachedSourcesArray[i];

            try
            {
                // Only mix playing sources
                if (source.State != AudioState.Playing)
                    continue;

                // PRIORITY 1: NEW - IMasterClockSource (if attached to clock)
                if (source is IMasterClockSource clockSource && clockSource.IsAttachedToClock)
                {
                    bool success = clockSource.ReadSamplesAtTime(
                        timestamp,
                        sourceBuffer.AsSpan(),
                        _bufferSizeInFrames,
                        out ReadResult result);

                    // CRITICAL FIX: Always mix whatever we got (could be silence if underrun)
                    // This ensures track timing stays aligned even during dropouts
                    if (result.FramesRead > 0)
                    {
                        MixIntoBuffer(mixBuffer, sourceBuffer, result.FramesRead * _config.Channels);
                    }

                    if (success)
                    {
                        activeSources++;
                    }
                    else
                    {
                        // Dropout occurred - fire event
                        OnTrackDropout(new TrackDropoutEventArgs(
                            source.Id,
                            source.GetType().Name,
                            timestamp,
                            _masterClock.CurrentSamplePosition,
                            _bufferSizeInFrames - result.FramesRead,
                            result.ErrorMessage ?? "Buffer underrun"));

                        // Record dropout in metrics
                        lock (_metricsLock)
                        {
                            if (_trackMetrics.TryGetValue(source.Id, out var metrics))
                            {
                                metrics.RecordDropout(timestamp, _bufferSizeInFrames - result.FramesRead);
                            }
                        }
                    }
                }
                // PRIORITY 2: LEGACY - Standard IAudioSource (GhostTrack sync or standalone)
                else
                {
                    // Legacy path: use existing ReadSamples() method
                    int framesRead = source.ReadSamples(sourceBuffer, _bufferSizeInFrames);

                    if (framesRead > 0)
                    {
                        MixIntoBuffer(mixBuffer, sourceBuffer, framesRead * _config.Channels);
                        activeSources++;
                    }
                }
            }
            catch (Exception ex)
            {
                // Source error - report but continue mixing other sources
                OnSourceError(source, new AudioErrorEventArgs(
                    $"Error reading from source {source.Id}: {ex.Message}", ex));
            }
        }

        return activeSources;
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
                // Only mix playing sources
                if (source.State != AudioState.Playing)
                    continue;

                // In offline mode, we wait for tracks to be ready
                if (source is IMasterClockSource clockSource && clockSource.IsAttachedToClock)
                {
                    // Retry loop with timeout for offline rendering
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
                            // Wait briefly and retry
                            Thread.Sleep(1);
                            retryCount++;
                        }
                    }

                    if (success && result.FramesRead > 0)
                    {
                        MixIntoBuffer(mixBuffer, sourceBuffer, result.FramesRead * _config.Channels);
                        activeSources++;
                    }
                    else
                    {
                        // Timeout - report error
                        OnSourceError(source, new AudioErrorEventArgs(
                            $"Offline rendering timeout for source {source.Id} at timestamp {timestamp:F3}s", null));
                    }
                }
                else
                {
                    // Legacy path
                    int framesRead = source.ReadSamples(sourceBuffer, _bufferSizeInFrames);

                    if (framesRead > 0)
                    {
                        MixIntoBuffer(mixBuffer, sourceBuffer, framesRead * _config.Channels);
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
    /// Advances legacy GhostTracks (deprecated path for backward compatibility).
    /// LEGACY - deprecated but functional
    /// </summary>
    private void AdvanceLegacyGhostTracks(float[] sourceBuffer)
    {
        var syncGroupIds = _synchronizer.GetSyncGroupIds();
        foreach (var groupId in syncGroupIds)
        {
            var ghostTrack = _synchronizer.GetGhostTrack(groupId);
            // Only advance if playing
            if (ghostTrack != null && ghostTrack.State == AudioState.Playing)
            {
                // We use the existing sourceBuffer to read samples (which are silence)
                // This advances the GhostTrack's internal position
                ghostTrack.ReadSamples(sourceBuffer, _bufferSizeInFrames);
            }
        }
    }

    /// <summary>
    /// Fires the TrackDropout event (NEW - v2.4.0+).
    /// </summary>
    private void OnTrackDropout(TrackDropoutEventArgs e)
    {
        TrackDropout?.Invoke(this, e);
    }
}
