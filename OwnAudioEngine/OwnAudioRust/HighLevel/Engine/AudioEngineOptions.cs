using Ownaudio.Audio.Streams;

namespace Ownaudio.Audio;

/// <summary>
/// Initialization options passed to <see cref="AudioEngine.Create"/>.
/// </summary>
/// <remarks>
/// All properties have safe defaults suitable for desktop use at CD quality.
/// Only override values that differ from the host application's requirements.
/// </remarks>
public sealed class AudioEngineOptions
{
    #region Properties

    /// <summary>
    /// Default sample rate in Hz applied to players and recorders that do not specify one
    /// explicitly.  Valid range: 8 000 – 192 000.  Default: 44 100.
    /// </summary>
    public int DefaultSampleRate { get; init; } = 44_100;

    /// <summary>
    /// Default channel count applied to players and recorders that do not specify one
    /// explicitly.  Valid range: 1 – 32.  Default: 2 (stereo).
    /// </summary>
    public int DefaultChannels { get; init; } = 2;

    /// <summary>
    /// Default audio buffer size in frames.  0 lets the platform choose the optimal value.
    /// Valid non-zero range: 16 – 8 192.  Default: 0.
    /// </summary>
    public int DefaultBufferSizeFrames { get; init; } = 0;

    /// <summary>
    /// Default sample format used for new streams.
    /// Default: <see cref="SampleFormat.Float32"/>.
    /// </summary>
    public SampleFormat DefaultSampleFormat { get; init; } = SampleFormat.Float32;

    /// <summary>
    /// The audio host API to use for device access and stream creation, or
    /// <see langword="null"/> to use the platform default (WASAPI on Windows,
    /// Core Audio on macOS, ALSA on Linux).
    /// </summary>
    /// <remarks>
    /// There is no automatic fallback: if the requested host API is unavailable
    /// the engine throws <see cref="Safe.Exceptions.HostApiNotAvailableException"/>
    /// or <see cref="Safe.Exceptions.AsioDriverNotFoundException"/> rather than
    /// silently downgrading to a different backend.
    /// </remarks>
    public HostApi? PreferredHostApi { get; init; } = null;

    #endregion
}
