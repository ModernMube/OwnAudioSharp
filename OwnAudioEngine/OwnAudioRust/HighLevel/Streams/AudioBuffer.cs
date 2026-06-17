using System;

namespace Ownaudio.Audio.Streams;

/// <summary>
/// Managed-safe, zero-allocation view over an audio sample buffer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AudioBuffer"/> is a <see langword="ref struct"/> so the compiler prevents
/// storing it on the heap or capturing it in a lambda.  This guarantees that the underlying
/// native memory cannot outlive the callback that created it.
/// </para>
/// <para>
/// Do <b>not</b> store any reference to <see cref="Samples"/> beyond the scope of the
/// method that received this value — the memory is owned by the Rust audio thread and
/// will be reused or freed immediately after the callback returns.
/// </para>
/// </remarks>
public readonly ref struct AudioBuffer
{
    #region Properties

    /// <summary>
    /// Interleaved samples.  Write output here (in a fill callback) or read captured
    /// audio from here (in a capture callback).
    /// Length equals <c>FrameCount × Channels</c>.
    /// </summary>
    public Span<float> Samples { get; }

    /// <summary>Number of audio frames in this buffer.</summary>
    public int FrameCount { get; }

    /// <summary>Number of interleaved channels per frame.</summary>
    public int Channels { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Wraps an existing <see cref="Span{T}"/> as an <see cref="AudioBuffer"/>.
    /// </summary>
    /// <param name="samples">Interleaved sample data.</param>
    /// <param name="frameCount">Number of frames.</param>
    /// <param name="channels">Number of channels per frame.</param>
    public AudioBuffer(Span<float> samples, int frameCount, int channels)
    {
        Samples    = samples;
        FrameCount = frameCount;
        Channels   = channels;
    }

    #endregion
}
