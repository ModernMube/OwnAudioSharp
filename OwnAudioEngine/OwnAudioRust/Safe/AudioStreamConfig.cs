using Ownaudio.Native.RustAudio.Structs;
using Ownaudio.Safe.Validation;

namespace Ownaudio.Safe;

/// <summary>
/// Stream params checked once in the ctor, so nothing invalid ever reaches the ffi side.
/// </summary>
public sealed class AudioStreamConfig
{
    /// <summary>
    /// Rate in Hz, 8000 - 192000.
    /// </summary>
    public int SampleRate { get; }

    // 1 - 256
    public int Channels { get; }

    public SampleFormat SampleFormat { get; }

    /// <summary>
    /// Buffer size in frames, 16 - 8192. Zero means let the engine pick the platform default.
    /// </summary>
    public int BufferSizeFrames { get; }

    /// <summary>
    /// How deep the buffered render ring is, in frames. Zero keeps the engine default (~100ms).
    /// This is what a buffered stream costs in output latency on top of the device buffer, since
    /// the producer keeps the ring topped up. Only matters for buffered streams.
    /// </summary>
    public int RenderRingFrames { get; }

    /// <param name="bufferSizeFrames"></param>
    public AudioStreamConfig(int sampleRate, int channels, SampleFormat sampleFormat = SampleFormat.F32, int bufferSizeFrames = 0)
        : this(sampleRate, channels, sampleFormat, bufferSizeFrames, 0)
    {
    }

    /// <summary>
    /// Same, plus renderRingFrames: the buffered render ring depth, 0 for the engine default.
    /// The engine pulls anything shallower than three device buffers back up, so check
    /// AudioOutputStream.RingFrames for what you really got.
    /// </summary>
    /// <param name="renderRingFrames"></param>
    public AudioStreamConfig(int sampleRate, int channels, SampleFormat sampleFormat, int bufferSizeFrames, int renderRingFrames)
    {
        Guard.InRange(sampleRate, 8_000, 192_000, nameof(sampleRate));
        Guard.InRange(channels, 1, 256, nameof(channels));
        if (bufferSizeFrames != 0) Guard.InRange(bufferSizeFrames, 16, 8_192, nameof(bufferSizeFrames));
        if (renderRingFrames != 0) Guard.InRange(renderRingFrames, 16, 384_000, nameof(renderRingFrames));

        SampleRate       = sampleRate;
        Channels         = channels;
        SampleFormat     = sampleFormat;
        BufferSizeFrames = bufferSizeFrames;
        RenderRingFrames = renderRingFrames;
    }

    // blittable twin for the ffi call
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
}
