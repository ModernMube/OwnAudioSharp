using System;

namespace Ownaudio.Decoders;

/// <summary>
/// Anything that can hand us audio frames from some source.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>
    /// Channels, rate, duration of the loaded source.
    /// </summary>
    AudioStreamInfo StreamInfo { get; }

    /// <summary>
    /// Fills buffer with the next block of Float32 samples. This is the zero-alloc read path.
    /// </summary>
    /// <returns>Result carrying how many frames landed in the buffer.</returns>
    AudioDecoderResult ReadFrames(byte[] buffer);

    /// <summary>
    /// Jumps to position; false plus an error message if it couldn't.
    /// </summary>
    bool TrySeek(TimeSpan position, out string error);
}
