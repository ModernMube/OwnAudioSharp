using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.RustNext.Sources;

namespace OwnaudioNET.RustNext.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Adds an audio source to the mixer, optionally starting playback immediately.
    /// The source can be added while the mixer is running (hot-swap).
    /// Thread-safe; uses <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
    /// internally and rebuilds the sources cache on the calling (main) thread.
    /// </summary>
    /// <param name="source">The audio source to add.</param>
    /// <returns>
    /// <see langword="true"/> if the source was added successfully;
    /// <see langword="false"/> if it was already registered.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the mixer has reached the maximum source limit
    /// defined by <see cref="AudioConstants.MaxAudioSources"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public bool AddSource(IAudioSource source)
    {
        ThrowIfDisposed();

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (_sources.Count >= AudioConstants.MaxAudioSources)
        {
            throw new InvalidOperationException(
                $"Maximum track limit ({AudioConstants.MaxAudioSources}) reached. " +
                $"Cannot add more sources. This limit ensures acceptable CPU performance with SoundTouch processing.");
        }

        bool added = _sources.TryAdd(source.Id, source);

        if (added)
        {
            if (source is IMasterClockSource clockSource)
                clockSource.AttachToClock(_masterClock);

            source.Error += OnSourceError;
            RebuildSourcesCache();
            _playbackEndedFired = false;
            if (_isRunning && source.State != AudioState.Playing)
            {
                try
                {
                    source.Play();
                }
                catch {}
            }
        }

        return added;
    }

    /// <summary>
    /// Adds a source to the mixer without starting playback.
    /// Use this together with <see cref="FileSource.PreBuffer"/> and
    /// <see cref="StartPreparedSources"/> for zero-drift multi-track startup where
    /// all sources must begin exactly at the same master clock position.
    /// </summary>
    /// <param name="source">The audio source to add.</param>
    /// <returns>
    /// <see langword="true"/> if the source was added successfully;
    /// <see langword="false"/> if it was already registered.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the track limit is exceeded.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public bool AddSourcePrepared(IAudioSource source)
    {
        ThrowIfDisposed();

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (_sources.Count >= AudioConstants.MaxAudioSources)
            throw new InvalidOperationException(
                $"Maximum track limit ({AudioConstants.MaxAudioSources}) reached. " +
                $"Cannot add more sources.");

        bool added = _sources.TryAdd(source.Id, source);
        if (added)
        {
            if (source is IMasterClockSource clockSource)
                clockSource.AttachToClock(_masterClock);

            source.Error += OnSourceError;
            RebuildSourcesCache();
            _playbackEndedFired = false;
        }

        return added;
    }

    /// <summary>
    /// Removes an audio source from the mixer by reference and stops it gracefully.
    /// The sources cache is rebuilt on the calling thread after removal so the audio
    /// thread never performs a <c>ToArray()</c> allocation.
    /// </summary>
    /// <param name="source">The audio source to remove.</param>
    /// <returns>
    /// <see langword="true"/> if the source was found and removed;
    /// <see langword="false"/> if it was not registered.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public bool RemoveSource(IAudioSource source)
    {
        ThrowIfDisposed();

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        return RemoveSource(source.Id);
    }

    /// <summary>
    /// Removes an audio source from the mixer by its unique identifier and stops it gracefully.
    /// The sources cache is rebuilt on the calling thread after removal so the audio
    /// thread never performs a <c>ToArray()</c> allocation.
    /// </summary>
    /// <param name="sourceId">The <see cref="Guid"/> identifier of the source to remove.</param>
    /// <returns>
    /// <see langword="true"/> if the source was found and removed;
    /// <see langword="false"/> if no source with that ID was registered.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public bool RemoveSource(Guid sourceId)
    {
        ThrowIfDisposed();

        if (_sources.TryRemove(sourceId, out IAudioSource? source))
        {
            source.Error -= OnSourceError;

            if (source is IMasterClockSource clockSource)
                clockSource.DetachFromClock();

            RebuildSourcesCache();

            try
            {
                source.Stop();
            }
            catch {}

            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes all sources from the mixer, stopping each one before removal.
    /// The sources cache is rebuilt (to an empty array) on the calling thread
    /// after all sources have been cleared.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public void ClearSources()
    {
        ThrowIfDisposed();

        foreach (var source in _sources.Values)
        {
            try
            {
                if (source is IMasterClockSource clockSource)
                    clockSource.DetachFromClock();

                source.Error -= OnSourceError;
                source.Stop();
            }
            catch {}
        }

        _sources.Clear();
        RebuildSourcesCache();
    }

    /// <summary>
    /// Returns a point-in-time snapshot array of all currently registered audio sources.
    /// Each call allocates a new array; do not use this from the real-time audio thread.
    /// </summary>
    /// <returns>A new array containing all currently registered <see cref="IAudioSource"/> instances.</returns>
    public IAudioSource[] GetSources()
    {
        return _sources.Values.ToArray();
    }

    /// <summary>
    /// Handles error events raised by individual audio sources and forwards them
    /// to the mixer-level <see cref="AudioMixer.SourceError"/> event for subscriber notification.
    /// </summary>
    /// <param name="sender">The source object that raised the error, or <see langword="null"/>.</param>
    /// <param name="e">The error event arguments containing the exception and message.</param>
    private void OnSourceError(object? sender, AudioErrorEventArgs e)
    {
        SourceError?.Invoke(sender, e);
    }

    /// <summary>
    /// Builds a fresh point-in-time snapshot of the sources collection on the calling
    /// (main or control) thread, then publishes the new array to the audio thread via
    /// a single <see cref="Volatile.Write"/> so the mix loop can adopt it without any
    /// lock or heap allocation on the real-time thread.
    /// This method must be called exclusively from the main or control thread
    /// immediately after any mutation of the sources collection.
    /// </summary>
    private void RebuildSourcesCache()
    {
        var newArray = _sources.Values.ToArray();
        Volatile.Write(ref _pendingSourcesArray, newArray);
        _sourcesArrayNeedsUpdate = true;
    }

    /// <summary>
    /// Calculates and applies Plugin Delay Compensation across all registered sources.
    /// </summary>
    /// <remarks>
    /// Call this method after all sources have been added and before calling
    /// <see cref="AudioMixer.Start"/>. The algorithm:
    /// <list type="number">
    ///   <item>Collects <see cref="SourceWithEffects.EffectLatencySamples"/> from every
    ///         <see cref="SourceWithEffects"/> currently registered in the mixer.</item>
    ///   <item>Finds the maximum value — this is the track that arrives latest.</item>
    ///   <item>Delays every other track by <c>maxLatency − itsLatency</c> samples via
    ///         <see cref="SourceWithEffects.SetDelayCompensation"/> so all tracks are
    ///         sample-accurately aligned at the mixer output.</item>
    /// </list>
    /// Has no effect when no <see cref="SourceWithEffects"/> sources are registered or
    /// when all sources have identical (or zero) effect latency.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public void ApplyPluginDelayCompensation()
    {
        ThrowIfDisposed();

        int maxLatency = 0;
        int sourcesWithEffectsCount = 0;

        foreach (var src in _sources.Values)
        {
            if (src is SourceWithEffects swe)
            {
                int lat = swe.EffectLatencySamples;
                if (lat > maxLatency)
                    maxLatency = lat;
                sourcesWithEffectsCount++;
            }
        }

        if (sourcesWithEffectsCount == 0 || maxLatency == 0)
            return;

        foreach (var src in _sources.Values)
        {
            if (src is SourceWithEffects swe)
            {
                int compensation = maxLatency - swe.EffectLatencySamples;
                swe.SetDelayCompensation(compensation);
            }
        }
    }
}
