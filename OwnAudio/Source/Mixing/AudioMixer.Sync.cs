using OwnaudioNET.Interfaces;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Partial class of AudioMixer that implements synchronized multi-track playback.
/// This file contains all synchronization-related functionality separate from the core mixing implementation.
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>
    /// Gets the audio synchronizer instance.
    /// </summary>
    public AudioSynchronizer Synchronizer => _synchronizer;

    /// <summary>
    /// Creates a synchronization group with the specified sources.
    /// All sources in a group will be kept sample-accurate synchronized during playback.
    /// </summary>
    /// <param name="groupId">Unique identifier for the sync group (e.g., "karaoke", "multitrack1").</param>
    /// <param name="sources">Array of audio sources to synchronize.</param>
    /// <exception cref="ArgumentNullException">Thrown when groupId is null or empty.</exception>
    /// <exception cref="ArgumentException">Thrown when sources array is null or empty.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    /// <remarks>
    /// Example: mixer.CreateSyncGroup("karaoke", musicTrack, vocalsTrack, drumsTrack);
    /// All three tracks will start at exactly the same sample and stay synchronized.
    /// </remarks>
    public void CreateSyncGroup(string groupId, params IAudioSource[] sources)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            throw new ArgumentNullException(nameof(groupId));

        if (sources == null || sources.Length == 0)
            throw new ArgumentException("Sources array cannot be null or empty.", nameof(sources));

        // Ensure all sources are added to the mixer
        foreach (var source in sources)
        {
            if (!_sources.ContainsKey(source.Id))
            {
                AddSource(source);
            }
        }

        // Create sync group in synchronizer
        _synchronizer.CreateSyncGroup(groupId, sources);
    }

    /// <summary>
    /// Removes a synchronization group.
    /// </summary>
    /// <param name="groupId">The ID of the group to remove.</param>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void RemoveSyncGroup(string groupId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return;

        _synchronizer.RemoveSyncGroup(groupId);
    }

    /// <summary>
    /// Starts all sources in a sync group simultaneously at sample position 0.
    /// This ensures sample-accurate synchronized playback from the beginning.
    /// </summary>
    /// <param name="groupId">The ID of the sync group to start.</param>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    /// <remarks>
    /// All sources will be reset to position 0 and started simultaneously.
    /// The mixer must be running (Start() called) for audio output.
    /// </remarks>
    public void StartSyncGroup(string groupId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return;

        _synchronizer.SynchronizedStart(groupId);
    }

    /// <summary>
    /// Seeks all sources in a sync group to the same position simultaneously.
    /// Maintains sample-accurate synchronization after the seek.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <param name="positionInSeconds">The target position in seconds.</param>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    /// <remarks>
    /// All sources in the group will seek to the exact same time position.
    /// Synchronization is maintained after the seek operation.
    /// </remarks>
    public void SeekSyncGroup(string groupId, double positionInSeconds)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return;

        _synchronizer.SynchronizedSeek(groupId, positionInSeconds);
    }

    /// <summary>
    /// Pauses all sources in a sync group simultaneously.
    /// </summary>
    /// <param name="groupId">The ID of the sync group to pause.</param>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void PauseSyncGroup(string groupId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return;

        _synchronizer.SynchronizedPause(groupId);
    }

    /// <summary>
    /// Resumes all sources in a sync group simultaneously.
    /// </summary>
    /// <param name="groupId">The ID of the sync group to resume.</param>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void ResumeSyncGroup(string groupId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return;

        _synchronizer.SynchronizedResume(groupId);
    }

    /// <summary>
    /// Stops all sources in a sync group simultaneously.
    /// </summary>
    /// <param name="groupId">The ID of the sync group to stop.</param>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void StopSyncGroup(string groupId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return;

        _synchronizer.SynchronizedStop(groupId);
    }

    /// <summary>
    /// Gets all sources in a sync group.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <returns>A read-only collection of sources in the group, or an empty list if the group doesn't exist.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public IReadOnlyList<IAudioSource> GetSyncGroup(string groupId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return Array.Empty<IAudioSource>();

        return _synchronizer.GetSyncGroup(groupId);
    }

    /// <summary>
    /// Gets all sync group IDs currently registered in the mixer.
    /// </summary>
    /// <returns>A collection of sync group IDs.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public IReadOnlyCollection<string> GetSyncGroupIds()
    {
        ThrowIfDisposed();

        return _synchronizer.GetSyncGroupIds();
    }

    /// <summary>
    /// Checks for drift in all sync groups and resyncs if necessary.
    /// This is called automatically in the mixing loop, but can be called manually if needed.
    /// </summary>
    /// <param name="toleranceInFrames">Maximum allowed drift in frames before resyncing (default: 10).</param>
    public void CheckAndResyncAllGroups(int toleranceInFrames = 10)
    {
        ThrowIfDisposed();

        var groupIds = _synchronizer.GetSyncGroupIds();
        long bufferStartPosition = _synchronizer.MasterSamplePosition;

        foreach (var groupId in groupIds)
        {
            _synchronizer.CheckAndResyncGroup(groupId, bufferStartPosition, toleranceInFrames);
        }
    }

    /// <summary>
    /// Gets the ghost track (master sync clock) for a specific sync group.
    /// The ghost track is an invisible audio source that all other sources synchronize to.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <returns>The ghost track, or null if the group doesn't exist.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public Sources.GhostTrackSource? GetGhostTrack(string groupId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return null;

        return _synchronizer.GetGhostTrack(groupId);
    }

    /// <summary>
    /// Sets the tempo (playback speed) for all sources in a sync group.
    /// The tempo change is applied to the ghost track and cascades to all synchronized sources.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <param name="tempo">The tempo multiplier (1.0 = normal, 0.5 = half speed, 2.0 = double speed).</param>
    /// <returns>True if tempo was set successfully, false if group doesn't exist.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    /// <remarks>
    /// Example: mixer.SetSyncGroupTempo("multitrack", 1.5f); // Play 50% faster
    /// This ensures all tracks stay synchronized even at different playback speeds.
    /// </remarks>
    public bool SetSyncGroupTempo(string groupId, float tempo)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return false;

        return _synchronizer.SetGroupTempo(groupId, tempo);
    }

    /// <summary>
    /// Gets the current tempo for a sync group.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <returns>The tempo multiplier, or 1.0 if group doesn't exist.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public float GetSyncGroupTempo(string groupId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return 1.0f;

        return _synchronizer.GetGroupTempo(groupId);
    }

    /// <summary>
    /// Adds a source to an existing sync group and updates the ghost track length if needed.
    /// This is useful for dynamically adding tracks to an already playing sync group.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <param name="source">The source to add.</param>
    /// <returns>True if added successfully, false if group doesn't exist or source already in group.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    /// <remarks>
    /// The ghost track will automatically resize if the new source is longer than existing sources.
    /// The source will be synchronized to the current playback position of the group.
    /// </remarks>
    public bool AddSourceToSyncGroup(string groupId, IAudioSource source)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return false;

        if (source == null)
            return false;

        // Ensure source is added to mixer
        if (!_sources.ContainsKey(source.Id))
        {
            AddSource(source);
        }

        return _synchronizer.AddSourceToGroup(groupId, source);
    }

    /// <summary>
    /// Removes a source from a sync group and updates the ghost track length if needed.
    /// The ghost track may shrink if the removed source was the longest.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <param name="source">The source to remove.</param>
    /// <returns>True if removed successfully, false if group or source doesn't exist.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public bool RemoveSourceFromSyncGroup(string groupId, IAudioSource source)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(groupId))
            return false;

        if (source == null)
            return false;

        return _synchronizer.RemoveSourceFromGroup(groupId, source);
    }

    /// <summary>
    /// Gets the duration of a sync group based on the longest source.
    /// This represents the length of the longest audio source in the group.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <returns>The duration in seconds, or 0.0 if group doesn't exist or has no sources.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public double GetSyncGroupDuration(string groupId)
    {
        ThrowIfDisposed();

        var sources = GetSyncGroup(groupId);
        if (sources == null || sources.Count == 0)
            return 0.0;

        // Return the duration of the longest source
        return sources.Max(s => s.Duration);
    }

    /// <summary>
    /// Gets the current playback position of a sync group based on the longest source's position.
    /// This is more reliable than using the ghost track and avoids drift correction issues.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <returns>The position in seconds, or 0.0 if group doesn't exist or has no sources.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public double GetSyncGroupPosition(string groupId)
    {
        ThrowIfDisposed();

        var sources = GetSyncGroup(groupId);
        if (sources == null || sources.Count == 0)
            return 0.0;

        // Return the position of the longest source (which determines the total duration)
        // Find the source with maximum duration
        var longestSource = sources.OrderByDescending(s => s.Duration).FirstOrDefault();
        return longestSource?.Position ?? 0.0;
    }
}
