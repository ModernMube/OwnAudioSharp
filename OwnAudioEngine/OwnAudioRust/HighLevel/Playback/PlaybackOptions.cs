using Ownaudio.Audio.Streams;

namespace Ownaudio.Audio.Playback;

/// <summary>
/// Configuration options for an <see cref="AudioPlayer"/> instance.
/// </summary>
/// <remarks>
/// All properties have sensible defaults.  Pass <see langword="null"/> to
/// <see cref="AudioEngine.CreatePlayer"/> to use the defaults from
/// <see cref="AudioEngineOptions"/>.
/// </remarks>
public sealed class PlaybackOptions
{
    #region Properties

    /// <summary>
    /// Name of the output device to use, or <see langword="null"/> to use the system default.
    /// The name must exactly match a value returned by <see cref="Devices.AudioDeviceManager.PlaybackDevices"/>.
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Initial playback volume in the range [0, 1].  Default: 1.0 (full volume).
    /// </summary>
    public float Volume { get; init; } = 1.0f;

    /// <summary>
    /// When <see langword="true"/>, playback restarts from the beginning after reaching the end.
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool IsLooping { get; init; } = false;

    /// <summary>
    /// Requested audio buffer size in frames.  0 lets the platform choose.
    /// Valid non-zero range: 16 – 8 192.  Default: 0.
    /// </summary>
    public int BufferSizeFrames { get; init; } = 0;

    #endregion
}
