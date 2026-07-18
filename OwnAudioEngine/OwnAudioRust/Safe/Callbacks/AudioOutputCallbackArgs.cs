using System;

namespace Ownaudio.Safe.Callbacks;

/// <summary>
/// Output callback signature. Can't be an Action&lt;T&gt; because the args are a ref struct.
/// </summary>
public delegate void AudioOutputCallbackHandler(in AudioOutputCallbackArgs args);

/// <summary>
/// Writable view over the native output buffer. The memory belongs to the Rust audio
/// thread, so never hang on to it after the callback returns.
/// </summary>
public readonly ref struct AudioOutputCallbackArgs
{
    /// <summary>
    /// Interleaved samples the callback fills in. Comes in zeroed, FrameCount * Channels long.
    /// </summary>
    public Span<float> Buffer { get; }

    /// <summary>Frames in this cycle.</summary>
    public int FrameCount { get; }

    /// <summary>Interleaved channels per frame.</summary>
    public int Channels { get; }

    internal unsafe AudioOutputCallbackArgs(float* buffer, int frameCount, int channels)
    {
        FrameCount = frameCount;
        Channels = channels;
        Buffer = new Span<float>(buffer, frameCount * channels);
    }
}
