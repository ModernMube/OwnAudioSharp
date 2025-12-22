namespace OwnaudioNET.Core;

/// <summary>
/// Defines system-wide audio constants and limits.
/// These limits are enforced to ensure acceptable CPU performance with SoundTouch processing.
/// </summary>
public static class AudioConstants
{
    /// <summary>
    /// Maximum number of simultaneous audio sources.
    /// Enforced to ensure acceptable CPU performance with SoundTouch time-stretching.
    /// </summary>
    /// <remarks>
    /// With 25 tracks at 0.8x tempo, expected CPU usage is ~375% (4 cores @ 94%).
    /// This should be manageable on modern 4+ core CPUs.
    /// </remarks>
    public const int MaxAudioSources = 25;

    /// <summary>
    /// Minimum tempo multiplier (0.8x = -20% speed).
    /// Enforced to prevent excessive CPU load from extreme time-stretching.
    /// </summary>
    public const float MinTempo = 0.8f;

    /// <summary>
    /// Maximum tempo multiplier (1.2x = +20% speed).
    /// Enforced to prevent excessive CPU load from extreme time-stretching.
    /// </summary>
    public const float MaxTempo = 1.2f;
}
