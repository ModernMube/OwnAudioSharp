namespace OwnaudioNET.Interfaces;

/// <summary>
/// Audio source that can be kept sample-accurate with a sync group.
/// </summary>
public interface ISynchronizable
{
    /// <summary>
    /// Absolute sample position from the start of the audio.
    /// </summary>
    long SamplePosition { get; }

    /// <summary>
    /// Snap back to a sample position, used to fix drift between sources.
    /// </summary>
    /// <param name="samplePosition"></param>
    void ResyncTo(long samplePosition);

    /// <summary>
    /// Sync group this source belongs to; same id = kept together.
    /// </summary>
    string? SyncGroupId { get; set; }

    /// <summary>
    /// True while this source rides a group.
    /// </summary>
    bool IsSynchronized { get; set; }
}
