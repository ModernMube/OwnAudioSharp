namespace OwnaudioNET.Core;

/// <summary>
/// System-wide limits. These are here to keep SoundTouch from eating the CPU alive.
/// </summary>
public static class AudioConstants
{
    /// <summary>
    /// How many sources may play at once.
    /// </summary>
    /// <remarks>
    /// 25 tracks at 0.8x runs around 375% CPU, so roughly 4 cores at 94%. Fine on
    /// anything modern, not much headroom above that.
    /// </remarks>
    public const int MaxAudioSources = 25;

    /// <summary>
    /// Slowest we allow, -20%.
    /// </summary>
    public const float MinTempo = 0.8f;

    /// <summary>
    /// Fastest we allow, +20%. Beyond this the stretcher gets expensive.
    /// </summary>
    public const float MaxTempo = 1.2f;
}
