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

        // HARD LIMIT: Enforce maximum track count for CPU performance
        if (_sources.Count >= AudioConstants.MaxAudioSources)
        {
            throw new InvalidOperationException(
                $"Maximum track limit ({AudioConstants.MaxAudioSources}) reached. " +
                $"Cannot add more sources. This limit ensures acceptable CPU performance with SoundTouch processing.");
        }

        // Add to source dictionary
        bool added = _sources.TryAdd(source.Id, source);

        if (added)
        {
            // Subscribe to source error events
            source.Error += OnSourceError;

            // OPTIMIZATION: Invalidate cached array when sources change
            _sourcesArrayNeedsUpdate = true;

            // If mixer is running and source is not playing, start it
            if (_isRunning && source.State != AudioState.Playing)
            {
                try
                {
                    source.Play();
                }
                catch
                {
                    // Source failed to start - will be handled by error event
                }
            }
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
            // Unsubscribe from error events
            source.Error -= OnSourceError;

            // OPTIMIZATION: Invalidate cached array when sources change
            _sourcesArrayNeedsUpdate = true;

            // Stop the source
            try
            {
                source.Stop();
            }
            catch
            {
                // Ignore errors when stopping source
            }

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
            catch
            {
                // Ignore errors
            }
        }

        _sources.Clear();

        // OPTIMIZATION: Invalidate cached array when sources change
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
