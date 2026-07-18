using Ownaudio.Core;
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

        bool _added = _sources.TryAdd(source.Id, source);

        if (_added)
        {
            if (source is IMasterClockSource clockSource)
                clockSource.AttachToClock(_masterClock);

            if (_rustNative)
            {
                _attachSourceToRustSession(source);
                _ensureRustOutputAfterAttach();
            }

            source.Error += _onSourceError;
            _rebuildSourcesCache();

            if (_isRunning && source.State != AudioState.Playing)
            {
                try { source.Play(); }
                catch {}
            }
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

        bool _added = _sources.TryAdd(source.Id, source);
        if (_added)
        {
            if (source is IMasterClockSource clockSource)
                clockSource.AttachToClock(_masterClock);

            if (_rustNative)
            {
                _attachSourceToRustSession(source);
                _ensureRustOutputAfterAttach();
            }

            source.Error += _onSourceError;
            _rebuildSourcesCache();
        }

        return _added;
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
            catch {}

            if (_rustNative) _detachSourceFromRustSession(source);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Stops and drops every registered source.
    /// </summary>
    public void ClearSources()
    {
        _throwIfDisposed();

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
            catch {}
        }

        _sources.Clear();
        _rebuildSourcesCache();
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
            return;

        foreach (var src in _sources.Values)
        {
            if (src is SourceWithEffects swe)
                swe.SetDelayCompensation(_maxLatency - swe.EffectLatencySamples);
        }
    }
}
