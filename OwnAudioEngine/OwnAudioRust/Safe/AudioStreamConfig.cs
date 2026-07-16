using Ownaudio.Native.RustAudio.Structs;
using Ownaudio.Safe.Validation;

namespace Ownaudio.Safe;

/// <summary>
/// Immutable, validated configuration for opening an audio stream.
/// All parameters are validated in the constructor so that no invalid configuration
/// can ever reach the native layer.
/// </summary>
public sealed class AudioStreamConfig
{
    #region Properties

    /// <summary>Target sample rate in Hz. Valid range: 8 000 – 192 000.</summary>
    public int SampleRate { get; }

    /// <summary>Number of channels. Valid range: 1 – 256.</summary>
    public int Channels { get; }

    /// <summary>Sample data format used for the audio buffer.</summary>
    public SampleFormat SampleFormat { get; }

    /// <summary>
    /// Requested buffer size in audio frames.
    /// Valid range: 16 – 8 192; use 0 to let the engine choose the platform default.
    /// </summary>
    public int BufferSizeFrames { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new <see cref="AudioStreamConfig"/> with explicit validation.
    /// </summary>
    /// <param name="sampleRate">Target sample rate in Hz (8 000 – 192 000).</param>
    /// <param name="channels">Number of channels (1 – 256).</param>
    /// <param name="sampleFormat">Sample data format.</param>
    /// <param name="bufferSizeFrames">
    /// Buffer size in frames (16 – 8 192), or 0 for the platform default.
    /// </param>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// Thrown when any parameter is outside its valid range.
    /// </exception>
    public AudioStreamConfig(
        int sampleRate,
        int channels,
        SampleFormat sampleFormat = SampleFormat.F32,
        int bufferSizeFrames = 0)
    {
        Guard.InRange(sampleRate, 8_000, 192_000, nameof(sampleRate));
        Guard.InRange(channels, 1, 256, nameof(channels));

        if (bufferSizeFrames != 0)
        {
            Guard.InRange(bufferSizeFrames, 16, 8_192, nameof(bufferSizeFrames));
        }

        SampleRate      = sampleRate;
        Channels        = channels;
        SampleFormat    = sampleFormat;
        BufferSizeFrames = bufferSizeFrames;
    }

    #endregion

    #region Internal conversion

    /// <summary>
    /// Converts this configuration to the blittable <see cref="NativeStreamConfig"/>
    /// required by the FFI layer.
    /// </summary>
    internal NativeStreamConfig ToNative()
    {
        return new NativeStreamConfig
        {
            SampleRate       = (uint)SampleRate,
            Channels         = (ushort)Channels,
            SampleFormat     = (NativeSampleFormat)SampleFormat,
            BufferSizeFrames = (uint)BufferSizeFrames,
        };
    }

    #endregion
}
