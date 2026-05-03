using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Adds an audio source to the mixer.
    /// The source can be added while the mixer is running (hot-swap).
    /// </summary>
    /// <param name="source">The audio source to add.</param>
    /// <returns>True if added successfully, false if source already exists.</returns>
    /// <exception cref="ArgumentNullException">Thrown when source is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when source format doesn't match mixer format or track limit exceeded.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
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
            source.Error += OnSourceError;
            _sourcesArrayNeedsUpdate = true;
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
    /// Adds a source to the mixer WITHOUT starting playback.
    /// Use this together with <see cref="FileSource.PreBuffer"/> and
    /// <see cref="StartPreparedSources"/> for zero-drift multi-track startup.
    /// </summary>
    /// <param name="source">The audio source to add.</param>
    /// <returns>True if added successfully, false if source already exists.</returns>
    /// <exception cref="ArgumentNullException">Thrown when source is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when track limit exceeded.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
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
            source.Error += OnSourceError;
            _sourcesArrayNeedsUpdate = true;
            _playbackEndedFired = false;
        }

        return added;
    }

    /// <summary>
    /// Removes an audio source from the mixer.
    /// </summary>
    /// <param name="source">The audio source to remove.</param>
    /// <returns>True if removed successfully, false if source was not found.</returns>
    /// <exception cref="ArgumentNullException">Thrown when source is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public bool RemoveSource(IAudioSource source)
    {
        ThrowIfDisposed();

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        return RemoveSource(source.Id);
    }

    /// <summary>
    /// Removes an audio source from the mixer by its ID.
    /// </summary>
    /// <param name="sourceId">The ID of the source to remove.</param>
    /// <returns>True if removed successfully, false if source was not found.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public bool RemoveSource(Guid sourceId)
    {
        ThrowIfDisposed();

        if (_sources.TryRemove(sourceId, out IAudioSource? source))
        {
            source.Error -= OnSourceError;
            _sourcesArrayNeedsUpdate = true;

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
    /// Removes all sources from the mixer.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void ClearSources()
    {
        ThrowIfDisposed();

        foreach (var source in _sources.Values)
        {
            try
            {
                source.Error -= OnSourceError;
                source.Stop();
            }
            catch {}
        }

        _sources.Clear();
        _sourcesArrayNeedsUpdate = true;
    }

    /// <summary>
    /// Gets all active sources.
    /// </summary>
    /// <returns>Array of active sources (snapshot).</returns>
    public IAudioSource[] GetSources()
    {
        return _sources.Values.ToArray();
    }

    /// <summary>
    /// Handles source error events.
    /// </summary>
    private void OnSourceError(object? sender, AudioErrorEventArgs e)
    {
        SourceError?.Invoke(sender, e);
    }
}
