using Ownaudio.Audio.Streams;

namespace Ownaudio.Audio.Devices;

/// <summary>
/// Immutable description of an audio input or output device.
/// </summary>
/// <remarks>
/// Instances are created by <see cref="AudioDeviceManager"/> and are valid only until the
/// next call to <see cref="AudioDeviceManager.Refresh"/>.  Do not cache device records across
/// refresh calls; always re-query the device list after a <see cref="AudioDeviceManager.DeviceListChanged"/> event.
/// </remarks>
public sealed record AudioDevice
{
    #region Properties

    /// <summary>
    /// Unique device identifier.  Equals the device's OS-reported name on all platforms.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Human-readable device name provided by the OS audio subsystem.</summary>
    public required string Name { get; init; }

    /// <summary>Indicates whether this device is used for playback or capture.</summary>
    public required AudioDeviceType Type { get; init; }

    /// <summary>
    /// <see langword="true"/> when this device is the system-default for its <see cref="Type"/>.
    /// </summary>
    public required bool IsDefault { get; init; }

    /// <summary>Maximum number of input channels supported by this device (0 for output-only).</summary>
    public required int MaxInputChannels { get; init; }

    /// <summary>Maximum number of output channels supported by this device (0 for input-only).</summary>
    public required int MaxOutputChannels { get; init; }

    /// <summary>The device's preferred (native) sample rate in Hz.</summary>
    public required int DefaultSampleRate { get; init; }

    #endregion

    #region Conversion helpers

    /// <summary>
    /// Returns an <see cref="AudioFormat"/> using this device's native sample rate and
    /// the appropriate maximum channel count for its type.
    /// </summary>
    public AudioFormat ToDefaultFormat()
    {
        int channels = Type == AudioDeviceType.Playback ? MaxOutputChannels : MaxInputChannels;
        return new AudioFormat(DefaultSampleRate, System.Math.Max(channels, 1));
    }

    #endregion
}
