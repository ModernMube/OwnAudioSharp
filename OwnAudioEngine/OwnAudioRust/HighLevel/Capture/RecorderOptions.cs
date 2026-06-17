using Ownaudio.Audio.Streams;

namespace Ownaudio.Audio.Capture;

/// <summary>
/// Configuration options for an <see cref="AudioRecorder"/> instance.
/// </summary>
public sealed class RecorderOptions
{
    #region Properties

    /// <summary>
    /// Name of the input device to use, or <see langword="null"/> to use the system default.
    /// The name must exactly match a value returned by <see cref="Devices.AudioDeviceManager.CaptureDevices"/>.
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Capture sample rate in Hz.  Valid range: 8 000 – 192 000.  Default: 44 100.
    /// </summary>
    public int SampleRate { get; init; } = 44_100;

    /// <summary>
    /// Number of capture channels.  Valid range: 1 – 32.  Default: 1 (mono).
    /// </summary>
    public int Channels { get; init; } = 1;

    /// <summary>
    /// Sample format for captured audio.  Default: <see cref="SampleFormat.Float32"/>.
    /// </summary>
    public SampleFormat SampleType { get; init; } = SampleFormat.Float32;

    /// <summary>
    /// Requested audio buffer size in frames.  0 lets the platform choose.
    /// Valid non-zero range: 16 – 8 192.  Default: 0.
    /// </summary>
    public int BufferSizeFrames { get; init; } = 0;

    #endregion
}
