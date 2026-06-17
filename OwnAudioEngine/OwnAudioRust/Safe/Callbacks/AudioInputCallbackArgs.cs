using System;

namespace Ownaudio.Safe.Callbacks;

/// <summary>
/// Delegate type for the input audio callback.
/// Use this instead of <c>Action&lt;AudioInputCallbackArgs&gt;</c> because
/// <see cref="AudioInputCallbackArgs"/> is a <see langword="ref struct"/> and cannot
/// be used as a generic type argument.
/// </summary>
/// <param name="args">
/// Zero-allocation read-only view over the captured buffer, valid only for the duration of this call.
/// Do not store or capture it beyond the callback scope.
/// </param>
public delegate void AudioInputCallbackHandler(in AudioInputCallbackArgs args);

/// <summary>
/// Zero-allocation read-only view over the native input audio buffer, valid only for the
/// duration of the callback invocation.
/// </summary>
/// <remarks>
/// <para>
/// This is a <see langword="readonly ref struct"/> to allow holding a <see cref="ReadOnlySpan{T}"/>
/// field while guaranteeing no heap allocation when passed by reference to the callback.
/// </para>
/// <para>
/// Writing to the underlying memory through unsafe pointer tricks results in undefined behaviour.
/// </para>
/// </remarks>
public readonly ref struct AudioInputCallbackArgs
{
    #region Properties

    /// <summary>
    /// Interleaved captured samples for this callback cycle (read-only view).
    /// Length equals <see cref="FrameCount"/> × <see cref="Channels"/>.
    /// </summary>
    public ReadOnlySpan<float> Buffer { get; }

    /// <summary>Number of audio frames in this callback cycle.</summary>
    public int FrameCount { get; }

    /// <summary>Number of interleaved channels per frame.</summary>
    public int Channels { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Constructs <see cref="AudioInputCallbackArgs"/> from a raw pointer received on the
    /// real-time audio thread.
    /// </summary>
    internal unsafe AudioInputCallbackArgs(float* buffer, int frameCount, int channels)
    {
        FrameCount = frameCount;
        Channels   = channels;
        Buffer     = new ReadOnlySpan<float>(buffer, frameCount * channels);
    }

    #endregion
}
