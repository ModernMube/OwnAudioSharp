using System;

namespace Ownaudio.Safe.Callbacks;

/// <summary>
/// Input callback signature. Can't be an Action&lt;T&gt; because the args are a ref struct.
/// </summary>
public delegate void AudioInputCallbackHandler(in AudioInputCallbackArgs args);

/// <summary>
/// Read-only peek at the native capture buffer. Only alive while the callback runs.
/// </summary>
public readonly ref struct AudioInputCallbackArgs
{
    /// <summary>
    /// Interleaved captured samples, FrameCount * Channels long.
    /// </summary>
    public ReadOnlySpan<float> Buffer { get; }

    /// <summary>Frames in this cycle.</summary>
    public int FrameCount { get; }

    /// <summary>Interleaved channels per frame.</summary>
    public int Channels { get; }

    internal unsafe AudioInputCallbackArgs(float* buffer, int frameCount, int channels)
    {
        FrameCount = frameCount;
        Channels = channels;
        Buffer = new ReadOnlySpan<float>(buffer, frameCount * channels);
    }
}
