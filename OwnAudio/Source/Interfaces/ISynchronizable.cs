namespace OwnaudioNET.Interfaces;

/// <summary>
/// Interface for audio sources that support sample-accurate synchronization.
/// Implements multi-track synchronization capabilities for precise timing control.
/// </summary>
public interface ISynchronizable
{
    /// <summary>
    /// Gets the current sample position in the audio stream.
    /// This value represents the absolute position in samples from the beginning of the audio.
    /// </summary>
    long SamplePosition { get; }

    /// <summary>
    /// Resyncs the audio source to a specific sample position.
    /// Used by the synchronizer to correct drift between multiple sources.
    /// </summary>
    /// <param name="samplePosition">The target sample position to sync to.</param>
    void ResyncTo(long samplePosition);

    /// <summary>
    /// Gets the synchronization group ID this source belongs to.
    /// Sources with the same group ID will be kept synchronized.
    /// </summary>
    string? SyncGroupId { get; set; }

    /// <summary>
    /// Gets or sets whether this source is currently synchronized with a group.
    /// </summary>
    bool IsSynchronized { get; set; }
}
