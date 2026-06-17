using System;

namespace Ownaudio.Audio.Capture;

/// <summary>
/// Provides data for the <see cref="AudioRecorder.DataAvailable"/> event.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Data"/> buffer is a managed copy of the native audio data.
/// It is safe to read after the event handler returns.
/// </para>
/// <para>
/// <b>Important:</b> This event fires on the real-time audio thread.
/// Do not perform blocking I/O, allocate large objects, or acquire locks inside
/// the event handler.  Use <c>Channel&lt;T&gt;</c> or a lock-free queue to hand
/// the data off to a processing thread.
/// </para>
/// </remarks>
public sealed class AudioDataAvailableEventArgs : EventArgs
{
    #region Properties

    /// <summary>
    /// Captured interleaved audio samples for this callback cycle.
    /// Length equals <c>FrameCount × Channels</c>.
    /// </summary>
    public ReadOnlyMemory<float> Data { get; }

    /// <summary>Number of audio frames in this buffer.</summary>
    public int FrameCount { get; }

    /// <summary>Number of interleaved channels per frame.</summary>
    public int Channels { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new <see cref="AudioDataAvailableEventArgs"/>.
    /// </summary>
    /// <param name="data">Managed copy of the captured samples.</param>
    /// <param name="frameCount">Number of frames in the buffer.</param>
    /// <param name="channels">Number of channels per frame.</param>
    public AudioDataAvailableEventArgs(
        ReadOnlyMemory<float> data,
        int frameCount,
        int channels)
    {
        Data       = data;
        FrameCount = frameCount;
        Channels   = channels;
    }

    #endregion
}
