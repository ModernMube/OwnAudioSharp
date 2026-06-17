using System;

namespace Ownaudio.Safe.Callbacks;

/// <summary>
/// Delegate type for the output audio callback.
/// Use this instead of <c>Action&lt;AudioOutputCallbackArgs&gt;</c> because
/// <see cref="AudioOutputCallbackArgs"/> is a <see langword="ref struct"/> and cannot
/// be used as a generic type argument.
/// </summary>
/// <param name="args">
/// Zero-allocation view over the output buffer, valid only for the duration of this call.
/// Do not store or capture it beyond the callback scope.
/// </param>
public delegate void AudioOutputCallbackHandler(in AudioOutputCallbackArgs args);

/// <summary>
/// Zero-allocation view over the native output audio buffer, valid only for the duration
/// of the callback invocation.
/// </summary>
/// <remarks>
/// <para>
/// This is a <see langword="readonly ref struct"/> to allow holding a <see cref="Span{T}"/>
/// field while guaranteeing no heap allocation when passed by reference to the callback.
/// </para>
/// <para>
/// Do not store or capture the <see cref="Buffer"/> span beyond the callback scope —
/// the underlying memory belongs to the Rust audio thread and will be reused or freed.
/// </para>
/// </remarks>
public readonly ref struct AudioOutputCallbackArgs
{
    #region Properties

    /// <summary>
    /// Interleaved output samples to be filled by the user callback.
    /// Length equals <see cref="FrameCount"/> × <see cref="Channels"/>.
    /// The buffer is zeroed before the callback is invoked.
    /// </summary>
    public Span<float> Buffer { get; }

    /// <summary>Number of audio frames in this callback cycle.</summary>
    public int FrameCount { get; }

    /// <summary>Number of interleaved channels per frame.</summary>
    public int Channels { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Constructs <see cref="AudioOutputCallbackArgs"/> from a raw pointer received on the
    /// real-time audio thread.
    /// </summary>
    internal unsafe AudioOutputCallbackArgs(float* buffer, int frameCount, int channels)
    {
        FrameCount = frameCount;
        Channels   = channels;
        Buffer     = new Span<float>(buffer, frameCount * channels);
    }

    #endregion
}
