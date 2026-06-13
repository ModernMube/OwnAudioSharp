using System.Runtime.CompilerServices;
using OwnaudioNET.Engine;
using OwnaudioNET.Synchronization;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Zero-allocation struct-based ThreadPool work item for firing the PlaybackEnded event.
/// Avoids the GC heap allocation caused by a lambda closure when using
/// <c>ThreadPool.QueueUserWorkItem(_ =&gt; ...)</c> on the real-time audio thread.
/// </summary>
file struct PlaybackEndedWorkItem : IThreadPoolWorkItem
{
    /// <summary>
    /// The mixer instance whose PlaybackEnded event will be raised.
    /// Stored as a direct reference; no boxing occurs because AudioMixer is a class.
    /// </summary>
    private readonly AudioMixer _mixer;

    /// <summary>
    /// Initializes a new instance of <see cref="PlaybackEndedWorkItem"/> with
    /// the target mixer instance that will receive the playback-ended notification.
    /// </summary>
    /// <param name="mixer">The mixer whose event should be fired.</param>
    public PlaybackEndedWorkItem(AudioMixer mixer) => _mixer = mixer;

    /// <summary>
    /// Executes the work item on the ThreadPool thread by invoking
    /// <see cref="AudioMixer.RaisePlaybackEnded"/> on the stored mixer instance.
    /// </summary>
    public void Execute() => _mixer.RaisePlaybackEnded();
}

public sealed partial class AudioMixer
{
    /// <summary>
    /// Main mix thread loop that continuously reads from all registered audio sources,
    /// blends them into a single output buffer, applies master effects, and sends
    /// the result to the audio engine.
    /// This is the hot real-time path and must remain strictly zero-allocation.
    /// Both pre-allocated mix and source buffers are reused across iterations.
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
                    var pending = Volatile.Read(ref _pendingSourcesArray);
                    if (pending != null)
                    {
                        _cachedSourcesArray = pending;
                        Volatile.Write(ref _pendingSourcesArray, null);
                    }
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
                        if (snapshot.Length > 0 && AllSourcesEnded(snapshot))
                        {
                            _playbackEndedFired = true;
                            ThreadPool.UnsafeQueueUserWorkItem(new PlaybackEndedWorkItem(this), preferLocal: false);
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
    /// Sends the mixed output buffer to the audio engine or to the
    /// <see cref="AudioEngineWrapper"/> circular buffer when pre-buffering is active.
    /// When an <see cref="AudioEngineWrapper"/> was provided at construction, output is routed
    /// through its internal circular buffer for decoupled pre-buffering.
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
    /// Mixes all registered sources in real-time mode using the master clock timestamp.
    /// Processing is sequential for stability; dropout events are reported through
    /// the <see cref="AudioMixer.TrackDropout"/> event when a source cannot supply
    /// samples in time. Introduced in v2.4.0 as part of the Master Clock System.
    /// </summary>
    /// <param name="mixBuffer">Destination mix buffer to accumulate samples into.</param>
    /// <param name="sourceBuffer">Temporary per-source read buffer (reused, zero-allocation).</param>
    /// <param name="timestamp">Current master clock position in seconds.</param>
    /// <param name="activeSources">Output: number of sources that supplied at least one frame.</param>
    /// <returns>
    /// <see langword="true"/> if any source experienced a dropout during this cycle;
    /// <see langword="false"/> otherwise.
    /// </returns>
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
    /// Mixes all registered sources in offline (non-real-time) mode using blocking waits.
    /// Each master-clock-attached source is polled until it can supply the required frames
    /// or until the 5-second timeout elapses. Introduced in v2.4.0 as part of the
    /// Master Clock System for deterministic offline rendering scenarios.
    /// </summary>
    /// <param name="mixBuffer">Destination mix buffer to accumulate samples into.</param>
    /// <param name="sourceBuffer">Temporary per-source read buffer (reused, zero-allocation).</param>
    /// <param name="timestamp">Current master clock position in seconds.</param>
    /// <returns>Number of sources that supplied at least one frame during this cycle.</returns>
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
                    int maxRetries = 5000;

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
    /// Raises the <see cref="AudioMixer.TrackDropout"/> event with the provided arguments.
    /// Introduced in v2.4.0 as part of the Master Clock dropout-detection pipeline.
    /// </summary>
    /// <param name="e">Event arguments describing the dropout that occurred.</param>
    private void OnTrackDropout(TrackDropoutEventArgs e)
    {
        TrackDropout?.Invoke(this, e);
    }

    /// <summary>
    /// Resynchronizes all non-clock-attached sources to the given master clock timestamp.
    /// Sources that implement <see cref="IMasterClockSource"/> and are attached to the clock
    /// perform self-correction internally and are therefore skipped here.
    /// </summary>
    /// <param name="masterTimestamp">Current MasterClock position in seconds.</param>
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

    /// <summary>
    /// Determines whether every source in the provided snapshot array has reached
    /// the <see cref="AudioState.EndOfStream"/> state.
    /// Implemented as a plain for-loop to avoid the delegate allocation that
    /// <c>Array.TrueForAll</c> would incur on each call from the real-time audio thread.
    /// </summary>
    /// <param name="sources">Snapshot array of sources to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when all sources are in <see cref="AudioState.EndOfStream"/>;
    /// <see langword="false"/> when at least one source is still active.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllSourcesEnded(IAudioSource[] sources)
    {
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i].State != AudioState.EndOfStream)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Raises the <see cref="AudioMixer.PlaybackEnded"/> event on the calling thread.
    /// Intended to be invoked exclusively from <see cref="PlaybackEndedWorkItem.Execute"/>
    /// running on a ThreadPool thread, keeping event dispatch off the real-time audio thread.
    /// </summary>
    internal void RaisePlaybackEnded()
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }
}
