using System.Diagnostics;
using Logger;
using Ownaudio.Core;
using Ownaudio.Safe.Exceptions;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Registers a source and starts it right away when the mixer is already running (hot-swap).
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    public bool AddSource(IAudioSource source)
    {
        _throwIfDisposed();

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (_sources.Count >= AudioConstants.MaxAudioSources)
            throw new InvalidOperationException(
                $"Maximum track limit ({AudioConstants.MaxAudioSources}) reached. " +
                $"Cannot add more sources. This limit ensures acceptable CPU performance with SoundTouch processing.");

        _throwIfGroupRateMismatch(source);

        bool _added = _sources.TryAdd(source.Id, source);

        if (_added)
        {
            _attachOrForget(source);

            source.Error += _onSourceError;
            _rebuildSourcesCache();

            Log.Info($"[Mixer] Source '{source.Id}' added ({_sources.Count} total)");

            if (_isRunning && source.State != AudioState.Playing && FollowsTransport(source))
            {
                //On the clock first, otherwise it enters from the head of the file
                AlignRustSourceToClock(source);

                try { source.Play(); }
                catch (Exception ex) { Log.Error($"[Mixer] Hot-swapped source '{source.Id}' failed to start", ex); }
            }
        }
        else
        {
            Log.Warning($"[Mixer] Source '{source.Id}' is already registered, add ignored");
        }

        return _added;
    }

    /// <summary>
    /// Registers a source but leaves it stopped. Use it with PreBuffer + StartPreparedSources
    /// when every track has to enter at the exact same clock position.
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    public bool AddSourcePrepared(IAudioSource source)
    {
        _throwIfDisposed();

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (_sources.Count >= AudioConstants.MaxAudioSources)
            throw new InvalidOperationException(
                $"Maximum track limit ({AudioConstants.MaxAudioSources}) reached. " +
                $"Cannot add more sources.");

        _throwIfGroupRateMismatch(source);

        bool _added = _sources.TryAdd(source.Id, source);
        if (_added)
        {
            _attachOrForget(source);

            source.Error += _onSourceError;
            _rebuildSourcesCache();

            Log.Info($"[Mixer] Source '{source.Id}' added prepared ({_sources.Count} total)");
        }
        else
        {
            Log.Warning($"[Mixer] Source '{source.Id}' is already registered, add ignored");
        }

        return _added;
    }

    /// <summary>
    /// The whole line-up at once, each source left stopped as AddSourcePrepared leaves it. A full
    /// command queue is waited out a device block at a time while the output drains it, so a big
    /// song goes on without the caller pacing it. Returns how many were added.
    /// </summary>
    /// <param name="sources"></param>
    public int AddSourcesPrepared(IEnumerable<IAudioSource> sources)
    {
        _throwIfDisposed();
        ArgumentNullException.ThrowIfNull(sources);

        int _added = 0;
        foreach (IAudioSource source in sources)
            if (_withQueueRoom(() => AddSourcePrepared(source))) _added++;

        return _added;
    }

    /// <summary>
    /// Longest a full queue is waited out before the refusal goes to the caller.
    /// </summary>
    private const int QueueRoomWaitMs = 1000;

    /// <summary>
    /// Tries again a block later while something drains the queue — the output stream renders,
    /// and each render empties it. Without an output nothing would, so it fails straight away.
    /// </summary>
    private bool _withQueueRoom(Func<bool> add)
    {
        var _waited = Stopwatch.StartNew();

        while (true)
        {
            try { return add(); }
            catch (CommandQueueFullException) when (_rustOutputStream is not null && _waited.ElapsedMilliseconds < QueueRoomWaitMs)
            {
                Thread.Sleep(Math.Max(1, _config.BufferSize * 1000 / Math.Max(1, _config.SampleRate)));
            }
        }
    }

    /// <summary>
    /// Clock and native track for a freshly registered source. When the engine refuses it, the
    /// mixer forgets it again, so the caller can simply ask again — nothing to take off first.
    /// </summary>
    /// <param name="source"></param>
    private void _attachOrForget(IAudioSource source)
    {
        if (source is IMasterClockSource clockSource)
            clockSource.AttachToClock(_masterClock);

        if (!_rustNative) return;

        try
        {
            _attachSourceToRustSession(source);
            _ensureRustOutputAfterAttach();
        }
        catch
        {
            _sources.TryRemove(source.Id, out _);

            if (source is IMasterClockSource clock)
                clock.DetachFromClock();

            try { _detachSourceFromRustSession(source); }
            catch (Exception ex) { Log.Error($"[Mixer] Refused source '{source.Id}' left a native track behind", ex); }

            throw;
        }
    }

    /// <summary>
    /// Removes a source by reference and stops it.
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    public bool RemoveSource(IAudioSource source)
    {
        _throwIfDisposed();

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        return RemoveSource(source.Id);
    }

    /// <summary>
    /// Removes a source by id and stops it. The snapshot is rebuilt here on the caller
    /// thread so the sync tick never has to allocate one.
    /// </summary>
    /// <param name="sourceId"></param>
    /// <returns></returns>
    public bool RemoveSource(Guid sourceId)
    {
        _throwIfDisposed();

        if (_sources.TryRemove(sourceId, out IAudioSource? source))
        {
            source.Error -= _onSourceError;

            if (source is IMasterClockSource clockSource)
                clockSource.DetachFromClock();

            _rebuildSourcesCache();

            try { source.Stop(); }
            catch (Exception ex) { Log.Error($"[Mixer] Source '{sourceId}' refused to stop while being removed", ex); }

            if (_rustNative) _detachSourceFromRustSession(source);

            Log.Info($"[Mixer] Source '{sourceId}' removed ({_sources.Count} left)");
            return true;
        }

        Log.Warning($"[Mixer] Remove asked for an unknown source '{sourceId}'");
        return false;
    }

    /// <summary>
    /// Stops and drops every registered source.
    /// </summary>
    public void ClearSources()
    {
        _throwIfDisposed();

        int _cleared = _sources.Count;

        foreach (var source in _sources.Values)
        {
            try
            {
                if (source is IMasterClockSource clockSource)
                    clockSource.DetachFromClock();

                source.Error -= _onSourceError;
                source.Stop();

                if (_rustNative) _detachSourceFromRustSession(source);
            }
            catch (Exception ex)
            {
                Log.Error($"[Mixer] Detaching source '{source.Id}' failed, dropping it anyway", ex);
            }
        }

        _sources.Clear();
        _rebuildSourcesCache();

        if (_cleared > 0) Log.Info($"[Mixer] {_cleared} sources cleared");
    }

    /// <summary>
    /// Snapshot of the registered sources. Allocates, so keep it off the audio thread.
    /// </summary>
    /// <returns></returns>
    public IAudioSource[] GetSources()
    {
        return _sources.Values.ToArray();
    }

    /// <summary>
    /// Bubbles a source error up to the mixer-level SourceError event.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void _onSourceError(object? sender, AudioErrorEventArgs e)
    {
        SourceError?.Invoke(sender, e);
    }

    /// <summary>
    /// Republishes the source snapshot the rust tick iterates, so the tick never touches
    /// the dictionary. Called right after any add/remove.
    /// </summary>
    private void _rebuildSourcesCache()
    {
        Volatile.Write(ref _rustSourceSnapshot, _sources.Values.ToArray());
    }

    /// <summary>
    /// Aligns every effect-wrapped track to the slowest one: each gets delayed by
    /// (maxLatency - itsLatency) samples so they land together at the output.
    /// Call it after all sources are in and before Start().
    /// </summary>
    [Obsolete("The PDC ring it sets up only runs in the managed SourceWithEffects.ReadSamples path, which " +
        "the rust-native chain bypasses — calling this changes nothing you can hear. A hosted VST3 stays " +
        "time-aligned on its own through the native bypass.")]
    public void ApplyPluginDelayCompensation()
    {
        _throwIfDisposed();

        int _maxLatency = 0;
        int _count = 0;

        foreach (var src in _sources.Values)
        {
            if (src is SourceWithEffects swe)
            {
                int _lat = swe.EffectLatencySamples;
                if (_lat > _maxLatency) _maxLatency = _lat;
                _count++;
            }
        }

        if (_count == 0 || _maxLatency == 0)
        {
            Log.Info($"[Mixer] Plugin delay compensation skipped: {_count} effect tracks, max latency {_maxLatency} samples");
            return;
        }

        foreach (var src in _sources.Values)
        {
            if (src is SourceWithEffects swe)
            {
#pragma warning disable CS0618
                swe.SetDelayCompensation(_maxLatency - swe.EffectLatencySamples);
#pragma warning restore CS0618
            }
        }

        Log.Info($"[Mixer] Plugin delay compensation applied to {_count} tracks, aligned to {_maxLatency} samples");
    }
}
