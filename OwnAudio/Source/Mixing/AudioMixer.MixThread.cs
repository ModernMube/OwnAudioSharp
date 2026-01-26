using System.Runtime.CompilerServices;
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
        // Pre-allocate buffers (done once, outside loop)
        int bufferSizeInSamples = _bufferSizeInFrames * _config.Channels;
        float[] mixBuffer = new float[bufferSizeInSamples];
        float[] sourceBuffer = new float[bufferSizeInSamples];

        // Initialize parallel buffers
        int procCount = Environment.ProcessorCount;
        lock (_parallelMixLock)
        {
            _parallelMixBuffers = new float[procCount][];
            _parallelReadBuffers = new float[procCount][];
            for (int i = 0; i < procCount; i++)
            {
                _parallelMixBuffers[i] = new float[bufferSizeInSamples];
                _parallelReadBuffers[i] = new float[bufferSizeInSamples];
            }
        }

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

                // Check buffer size consistency
                if (_parallelMixBuffers[0].Length != bufferSizeInSamples)
                {
                    lock (_parallelMixLock)
                    {
                        for (int i = 0; i < procCount; i++)
                        {
                            _parallelMixBuffers[i] = new float[bufferSizeInSamples];
                            _parallelReadBuffers[i] = new float[bufferSizeInSamples];
                        }
                    }
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
                bool dropoutDetected = false;

                if (_masterClock.Mode == ClockMode.Realtime)
                {
                    // Realtime mode: Non-blocking, dropouts → silence + event
                    dropoutDetected = MixSourcesRealtime(mixBuffer, sourceBuffer, currentTimestamp, out activeSources);

                    // OPTIMIZATION (Phase 1): Global resync removed to prevent "Thundering Herd"
                    // The FileSource's built-in "Three-Zone" drift correction (Green/Yellow/Red)
                    // handles individual track recovery more efficiently without causing
                    // massive CPU spikes from 20+ simultaneous Seek() operations.
                    // Each track self-corrects using:
                    //   - Green Zone (< 20ms): No correction
                    //   - Yellow Zone (20-100ms): Soft sync via tempo adjustment
                    //   - Red Zone (> 100ms): Buffer skip or predictive seek
                    // This approach prevents cascade failures and maintains stable CPU usage.
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
    /// Reverted to sequential processing for stability.
    /// </summary>
    /// <returns>True if any dropout was detected, false otherwise</returns>
    private bool MixSourcesRealtime(float[] mixBuffer, float[] sourceBuffer, double timestamp, out int activeSources)
    {
        activeSources = 0;
        bool dropoutDetected = false;

        // SEQUENTIAL MIXING (Restored for stability)
        // Parallel.For caused thread scheduling jitter which led to sync drift and dropouts.
        // The overhead of managing threads for each mix cycle outweighed the benefits.

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
                        // Check if source has custom channel mapping
                        if (source is FileSource fs && fs.OutputChannelMapping != null)
                        {
                            int sourceSampleCount = result.FramesRead * fs.Config.Channels;
                            MixIntoBufferSelective(
                                mixBuffer,
                                sourceBuffer,
                                sourceSampleCount,
                                fs.OutputChannelMapping,
                                _config.Channels);
                        }
                        else
                        {
                            // Default: mix to all channels
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
                        // Check if source has custom channel mapping
                        if (source is FileSource fs && fs.OutputChannelMapping != null)
                        {
                            int sourceSampleCount = framesRead * fs.Config.Channels;
                            MixIntoBufferSelective(
                                mixBuffer,
                                sourceBuffer,
                                sourceSampleCount,
                                fs.OutputChannelMapping,
                                _config.Channels);
                        }
                        else
                        {
                            // Default: mix to all channels
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
                        // Check if source has custom channel mapping
                        if (source is FileSource fs && fs.OutputChannelMapping != null)
                        {
                            int sourceSampleCount = result.FramesRead * fs.Config.Channels;
                            MixIntoBufferSelective(
                                mixBuffer,
                                sourceBuffer,
                                sourceSampleCount,
                                fs.OutputChannelMapping,
                                _config.Channels);
                        }
                        else
                        {
                            // Default: mix to all channels
                            MixIntoBuffer(mixBuffer, sourceBuffer, result.FramesRead * _config.Channels);
                        }
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
                        // Check if source has custom channel mapping
                        if (source is FileSource fs && fs.OutputChannelMapping != null)
                        {
                            int sourceSampleCount = framesRead * fs.Config.Channels;
                            MixIntoBufferSelective(
                                mixBuffer,
                                sourceBuffer,
                                sourceSampleCount,
                                fs.OutputChannelMapping,
                                _config.Channels);
                        }
                        else
                        {
                            // Default: mix to all channels
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

    /// <summary>
    /// Resynchronizes sources to the master timestamp.
    /// OPTIMIZATION (Phase 1): Modified to only affect legacy sources not attached to MasterClock.
    /// MasterClock sources self-correct via their built-in Three-Zone drift correction system.
    /// This prevents the "Thundering Herd" effect with 20+ tracks.
    /// </summary>
    /// <param name="masterTimestamp">Current MasterClock timestamp in seconds</param>
    private void ResyncSources(double masterTimestamp)
    {
        // OPTIMIZATION: Sequential iteration instead of Parallel.ForEach
        // Reduces CPU overhead and prevents simultaneous buffer clears
        foreach (var source in _cachedSourcesArray)
        {
            try
            {
                // Only resync playing sources
                if (source.State != AudioState.Playing)
                    continue;

                // CRITICAL: Skip MasterClock-attached sources
                // These sources have sophisticated drift correction and self-heal efficiently
                if (source is IMasterClockSource clockSource && clockSource.IsAttachedToClock)
                    continue;

                // Only legacy sources (GhostTrack sync or standalone) reach here
                source.Seek(masterTimestamp);
            }
            catch (Exception ex)
            {
                // Source error during resync - report but continue with other sources
                OnSourceError(source, new AudioErrorEventArgs(
                    $"Error resyncing source {source.Id}: {ex.Message}", ex));
            }
        }
    }
}
