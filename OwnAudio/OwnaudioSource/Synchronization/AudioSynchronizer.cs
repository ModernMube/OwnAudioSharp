using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Synchronization;

/// <summary>
/// Manages sample-accurate synchronization for multi-track audio playback using a "ghost track" as the master clock.
/// The ghost track is an invisible, silent audio source that acts as the master synchronization reference.
/// All other sources synchronize to this master track, ensuring perfect multi-track alignment even with tempo changes.
///
/// Architecture:
/// - Each sync group has its own ghost track
/// - Ghost track length = longest source in the group
/// - Ghost track automatically resizes when sources are added/removed
/// - All drift correction uses the ghost track as reference
/// </summary>
public sealed class AudioSynchronizer
{
    private readonly object _syncLock = new();
    private readonly Dictionary<IAudioSource, long> _sourcePositions = new();
    private readonly Dictionary<string, SyncGroupInfo> _syncGroups = new();
    private long _masterSamplePosition = 0;

    /// <summary>
    /// Holds information about a sync group including its ghost track and sources.
    /// </summary>
    private sealed class SyncGroupInfo
    {
        public GhostTrackSource GhostTrack { get; set; }
        public List<IAudioSource> Sources { get; set; }

        public SyncGroupInfo(GhostTrackSource ghostTrack)
        {
            GhostTrack = ghostTrack;
            Sources = new List<IAudioSource>();
        }
    }

    /// <summary>
    /// Gets the current master sample position.
    /// </summary>
    public long MasterSamplePosition => _masterSamplePosition;

    /// <summary>
    /// Registers an audio source for synchronization tracking.
    /// </summary>
    /// <param name="source">The audio source to register.</param>
    public void RegisterSource(IAudioSource source)
    {
        lock (_syncLock)
        {
            if (!_sourcePositions.ContainsKey(source))
            {
                _sourcePositions[source] = 0;
            }
        }
    }

    /// <summary>
    /// Unregisters an audio source from synchronization tracking.
    /// </summary>
    /// <param name="source">The audio source to unregister.</param>
    public void UnregisterSource(IAudioSource source)
    {
        lock (_syncLock)
        {
            _sourcePositions.Remove(source);

            // Remove from all sync groups and update ghost tracks
            foreach (var groupPair in _syncGroups)
            {
                var groupInfo = groupPair.Value;
                if (groupInfo.Sources.Remove(source))
                {
                    // Source was in this group - resize ghost track if needed
                    UpdateGhostTrackLength(groupInfo);
                }
            }
        }
    }

    /// <summary>
    /// Creates or updates a synchronization group with the specified sources.
    /// All sources in a group will be kept sample-accurate synchronized using a ghost track as master.
    /// </summary>
    /// <param name="groupId">The unique identifier for the sync group.</param>
    /// <param name="sources">The sources to include in the sync group.</param>
    public void CreateSyncGroup(string groupId, params IAudioSource[] sources)
    {
        lock (_syncLock)
        {
            SyncGroupInfo groupInfo;

            if (!_syncGroups.ContainsKey(groupId))
            {
                // Create new sync group with ghost track
                // Start with zero duration - will be updated when sources are added
                var ghostTrack = new GhostTrackSource(0.0, sampleRate: 48000, outputChannels: 2);
                groupInfo = new SyncGroupInfo(ghostTrack);
                _syncGroups[groupId] = groupInfo;
            }
            else
            {
                groupInfo = _syncGroups[groupId];
                groupInfo.Sources.Clear();
            }

            // Add all sources to the group
            foreach (var source in sources)
            {
                groupInfo.Sources.Add(source);
                RegisterSource(source);

                // Mark as synchronized if implements ISynchronizable
                if (source is ISynchronizable syncSource)
                {
                    syncSource.SyncGroupId = groupId;
                    syncSource.IsSynchronized = true;
                }
            }

            // Update ghost track to match longest source
            UpdateGhostTrackLength(groupInfo);
        }
    }

    /// <summary>
    /// Removes a synchronization group and disposes its ghost track.
    /// </summary>
    /// <param name="groupId">The ID of the group to remove.</param>
    public void RemoveSyncGroup(string groupId)
    {
        lock (_syncLock)
        {
            if (_syncGroups.TryGetValue(groupId, out var groupInfo))
            {
                // Unmark all sources
                foreach (var source in groupInfo.Sources)
                {
                    if (source is ISynchronizable syncSource)
                    {
                        syncSource.SyncGroupId = null;
                        syncSource.IsSynchronized = false;
                    }
                }

                // Dispose ghost track
                groupInfo.GhostTrack.Dispose();

                _syncGroups.Remove(groupId);
            }
        }
    }

    /// <summary>
    /// Starts all sources in a sync group simultaneously at sample position 0.
    /// Uses the ghost track as the master clock and a synchronization barrier to ensure sample-accurate start.
    /// </summary>
    /// <param name="groupId">The ID of the sync group to start.</param>
    public void SynchronizedStart(string groupId)
    {
        lock (_syncLock)
        {
            if (!_syncGroups.TryGetValue(groupId, out var groupInfo))
                return;

            var sources = groupInfo.Sources;
            var ghostTrack = groupInfo.GhostTrack;

            // STEP 1: Reset ghost track to position 0
            ghostTrack.Seek(0);
            ghostTrack.Play();

            // STEP 2: Close sync gates on all FileSource instances
            // This prevents the mixer from reading data while we're pre-buffering
            foreach (var source in sources)
            {
                if (source is OwnaudioNET.Sources.FileSource fileSource)
                {
                    fileSource.SetSyncGate(false); // Close gate - return silence during pre-buffer
                }
            }

            // STEP 3: Reset all sources to same position (seek to 0)
            // This is fast now because decoder threads haven't started yet
            foreach (var source in sources)
            {
                source.Seek(0);
                _sourcePositions[source] = 0;
            }

            _masterSamplePosition = 0;

            // STEP 4: Pre-start all decoder threads in parallel and wait for pre-buffering
            // This ensures all sources have data ready before we start playing
            var barrier = new System.Threading.CountdownEvent(sources.Count);
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            try
            {
                foreach (var source in sources)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            // Start decoder thread and pre-buffer
                            // Play() will start the decoder thread and wait for buffer fill
                            source.Play();

                            // Signal that this source is ready
                            barrier.Signal();
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                            barrier.Signal(); // Still signal to avoid deadlock
                        }
                    });
                }

                // Wait for all sources to pre-buffer (max 500ms total)
                if (!barrier.Wait(TimeSpan.FromMilliseconds(500)))
                {
                    // Timeout - some sources didn't pre-buffer in time
                    // Continue anyway to avoid hanging
                }

                // Check for errors
                if (!exceptions.IsEmpty)
                {
                    // At least one source failed - but we continue with the others
                }
            }
            finally
            {
                // Ensure barrier is disposed after all threads have finished
                barrier.Dispose();
            }

            // STEP 5: All sources are now pre-buffered with data at position 0
            // Open all gates simultaneously - this is the ATOMIC SYNC POINT
            foreach (var source in sources)
            {
                if (source is OwnaudioNET.Sources.FileSource fileSource)
                {
                    fileSource.SetSyncGate(true); // Open gate - allow reading
                }
            }

            // Now all sources will output synchronized audio starting from sample 0
            // Ghost track is running and acts as the master clock
        }
    }

    /// <summary>
    /// Seeks all sources in a sync group to the same position simultaneously.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <param name="positionInSeconds">The target position in seconds.</param>
    public void SynchronizedSeek(string groupId, double positionInSeconds)
    {
        lock (_syncLock)
        {
            if (!_syncGroups.TryGetValue(groupId, out var groupInfo) || groupInfo.Sources.Count == 0)
                return;

            var sources = groupInfo.Sources;
            var ghostTrack = groupInfo.GhostTrack;

            // Seek ghost track first
            ghostTrack.Seek(positionInSeconds);

            // Calculate sample position from first source's sample rate
            var firstSource = sources[0];
            long samplePosition = (long)(positionInSeconds * firstSource.Config.SampleRate);

            foreach (var source in sources)
            {
                source.Seek(positionInSeconds);
                _sourcePositions[source] = samplePosition;
            }

            _masterSamplePosition = samplePosition;
        }
    }

    /// <summary>
    /// Pauses all sources in a sync group simultaneously.
    /// </summary>
    /// <param name="groupId">The ID of the sync group to pause.</param>
    public void SynchronizedPause(string groupId)
    {
        lock (_syncLock)
        {
            if (!_syncGroups.TryGetValue(groupId, out var groupInfo))
                return;

            // Pause ghost track
            groupInfo.GhostTrack.Pause();

            // Pause all sources
            foreach (var source in groupInfo.Sources)
            {
                source.Pause();
            }
        }
    }

    /// <summary>
    /// Resumes all sources in a sync group simultaneously.
    /// </summary>
    /// <param name="groupId">The ID of the sync group to resume.</param>
    public void SynchronizedResume(string groupId)
    {
        lock (_syncLock)
        {
            if (!_syncGroups.TryGetValue(groupId, out var groupInfo))
                return;

            // Resume ghost track
            groupInfo.GhostTrack.Play();

            // Resume all sources
            foreach (var source in groupInfo.Sources)
            {
                source.Play();
            }
        }
    }

    /// <summary>
    /// Stops all sources in a sync group simultaneously.
    /// </summary>
    /// <param name="groupId">The ID of the sync group to stop.</param>
    public void SynchronizedStop(string groupId)
    {
        lock (_syncLock)
        {
            if (!_syncGroups.TryGetValue(groupId, out var groupInfo))
                return;

            // Stop ghost track
            groupInfo.GhostTrack.Stop();

            // Stop all sources
            foreach (var source in groupInfo.Sources)
            {
                source.Stop();
                _sourcePositions[source] = 0;
            }

            _masterSamplePosition = 0;
        }
    }

    /// <summary>
    /// Checks for drift between sources in a group and resyncs if necessary.
    /// Uses the ghost track as the master reference for synchronization.
    /// Should be called periodically during playback.
    /// </summary>
    /// <param name="groupId">The ID of the sync group to check.</param>
    /// <param name="bufferStartPosition">The current buffer start position in samples (optional, uses ghost track if not provided).</param>
    /// <param name="toleranceInFrames">Maximum allowed drift in frames before resyncing (default: 10).</param>
    public void CheckAndResyncGroup(string groupId, long bufferStartPosition, int toleranceInFrames = 10)
    {
        lock (_syncLock)
        {
            if (!_syncGroups.TryGetValue(groupId, out var groupInfo))
                return;

            // Use ghost track position as the master reference
            long masterPosition = groupInfo.GhostTrack.CurrentFrame;

            foreach (var source in groupInfo.Sources)
            {
                if (source is not ISynchronizable syncSource)
                    continue;

                // Calculate drift relative to ghost track
                long drift = syncSource.SamplePosition - masterPosition;

                // Convert tolerance to samples (assuming stereo, adjust if needed)
                long toleranceInSamples = toleranceInFrames * source.Config.Channels;

                if (Math.Abs(drift) > toleranceInSamples)
                {
                    // Drift detected, resync to ghost track position
                    syncSource.ResyncTo(masterPosition);
                    _sourcePositions[source] = masterPosition;
                }
            }
        }
    }

    /// <summary>
    /// Updates the position tracking for a source after reading samples.
    /// </summary>
    /// <param name="source">The source that read samples.</param>
    /// <param name="framesRead">The number of frames read.</param>
    public void UpdateSourcePosition(IAudioSource source, int framesRead)
    {
        lock (_syncLock)
        {
            if (_sourcePositions.ContainsKey(source))
            {
                _sourcePositions[source] += framesRead;
            }
        }
    }

    /// <summary>
    /// Advances the master sample position.
    /// Should be called after each mixing buffer is processed.
    /// </summary>
    /// <param name="frameCount">The number of frames processed.</param>
    public void AdvanceMasterPosition(int frameCount)
    {
        lock (_syncLock)
        {
            _masterSamplePosition += frameCount;
        }
    }

    /// <summary>
    /// Resets the synchronizer state.
    /// </summary>
    public void Reset()
    {
        lock (_syncLock)
        {
            _masterSamplePosition = 0;
            _sourcePositions.Clear();

            foreach (var groupInfo in _syncGroups.Values)
            {
                foreach (var source in groupInfo.Sources)
                {
                    if (source is ISynchronizable syncSource)
                    {
                        syncSource.SyncGroupId = null;
                        syncSource.IsSynchronized = false;
                    }
                }
                // Dispose ghost track
                groupInfo.GhostTrack.Dispose();
            }

            _syncGroups.Clear();
        }
    }

    /// <summary>
    /// Gets all sources in a sync group.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <returns>A read-only collection of sources in the group, or an empty list if the group doesn't exist.</returns>
    public IReadOnlyList<IAudioSource> GetSyncGroup(string groupId)
    {
        lock (_syncLock)
        {
            if (_syncGroups.TryGetValue(groupId, out var groupInfo))
            {
                return groupInfo.Sources.AsReadOnly();
            }
            return Array.Empty<IAudioSource>();
        }
    }

    /// <summary>
    /// Gets all sync group IDs.
    /// </summary>
    /// <returns>A collection of sync group IDs.</returns>
    public IReadOnlyCollection<string> GetSyncGroupIds()
    {
        lock (_syncLock)
        {
            return _syncGroups.Keys.ToArray();
        }
    }

    /// <summary>
    /// Gets the ghost track for a specific sync group.
    /// The ghost track is the master synchronization source for the group.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <returns>The ghost track, or null if the group doesn't exist.</returns>
    public GhostTrackSource? GetGhostTrack(string groupId)
    {
        lock (_syncLock)
        {
            if (_syncGroups.TryGetValue(groupId, out var groupInfo))
            {
                return groupInfo.GhostTrack;
            }
            return null;
        }
    }

    /// <summary>
    /// Adds a source to an existing sync group and updates the ghost track length if needed.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <param name="source">The source to add.</param>
    /// <returns>True if added successfully, false if group doesn't exist or source already in group.</returns>
    public bool AddSourceToGroup(string groupId, IAudioSource source)
    {
        lock (_syncLock)
        {
            if (!_syncGroups.TryGetValue(groupId, out var groupInfo))
                return false;

            if (groupInfo.Sources.Contains(source))
                return false;

            groupInfo.Sources.Add(source);
            RegisterSource(source);

            // Mark as synchronized
            if (source is ISynchronizable syncSource)
            {
                syncSource.SyncGroupId = groupId;
                syncSource.IsSynchronized = true;
            }

            // Update ghost track length if this source is longer
            UpdateGhostTrackLength(groupInfo);

            return true;
        }
    }

    /// <summary>
    /// Removes a source from a sync group and updates the ghost track length if needed.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <param name="source">The source to remove.</param>
    /// <returns>True if removed successfully, false if group or source doesn't exist.</returns>
    public bool RemoveSourceFromGroup(string groupId, IAudioSource source)
    {
        lock (_syncLock)
        {
            if (!_syncGroups.TryGetValue(groupId, out var groupInfo))
                return false;

            if (!groupInfo.Sources.Remove(source))
                return false;

            // Unmark as synchronized
            if (source is ISynchronizable syncSource)
            {
                syncSource.SyncGroupId = null;
                syncSource.IsSynchronized = false;
            }

            UnregisterSource(source);

            // Update ghost track length (may need to shrink)
            UpdateGhostTrackLength(groupInfo);

            return true;
        }
    }

    /// <summary>
    /// Sets the tempo for a sync group.
    /// This affects the ghost track and cascades to all synchronized sources.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <param name="tempo">The tempo multiplier (1.0 = normal, 0.5 = half speed, 2.0 = double speed).</param>
    /// <returns>True if tempo was set, false if group doesn't exist.</returns>
    public bool SetGroupTempo(string groupId, float tempo)
    {
        lock (_syncLock)
        {
            if (!_syncGroups.TryGetValue(groupId, out var groupInfo))
                return false;

            // Set tempo on ghost track
            groupInfo.GhostTrack.Tempo = tempo;

            // Set tempo on all sources that support it
            foreach (var source in groupInfo.Sources)
            {
                try
                {
                    source.Tempo = tempo;
                }
                catch
                {
                    // Some sources might not support tempo changes
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Gets the current tempo for a sync group.
    /// </summary>
    /// <param name="groupId">The ID of the sync group.</param>
    /// <returns>The tempo multiplier, or 1.0 if group doesn't exist.</returns>
    public float GetGroupTempo(string groupId)
    {
        lock (_syncLock)
        {
            if (_syncGroups.TryGetValue(groupId, out var groupInfo))
            {
                return groupInfo.GhostTrack.Tempo;
            }
            return 1.0f;
        }
    }

    /// <summary>
    /// Updates the ghost track length to match the longest source in the group.
    /// Called automatically when sources are added/removed.
    /// </summary>
    /// <param name="groupInfo">The sync group info.</param>
    private void UpdateGhostTrackLength(SyncGroupInfo groupInfo)
    {
        if (groupInfo.Sources.Count == 0)
        {
            // No sources - set ghost track to zero length
            groupInfo.GhostTrack.Resize(0.0);
            return;
        }

        // Find longest source duration
        double maxDuration = 0.0;
        foreach (var source in groupInfo.Sources)
        {
            if (source.Duration > maxDuration)
            {
                maxDuration = source.Duration;
            }
        }

        // Resize ghost track to match
        groupInfo.GhostTrack.Resize(maxDuration);
    }
}
