using System;

namespace Ownaudio.Decoders;

/// <summary>
/// An interface for decoding audio frames from given audio source.
/// <para>Implements: <see cref="IDisposable"/>.</para>
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>
    /// Gets the information about loaded audio source.
    /// </summary>
    AudioStreamInfo StreamInfo { get; }

    /// <summary>
    /// Decode next available audio frame from loaded audio source.
    /// This method allocates a new AudioFrame on every call and should be considered deprecated.
    /// </summary>
    /// <returns>A new <see cref="AudioDecoderResult"/> data.</returns>
    AudioDecoderResult DecodeNextFrame();

    /// <summary>
    /// Reads the next block of audio frames into the provided buffer.
    /// This is the recommended zero-allocation method for reading audio data.
    /// </summary>
    /// <param name="buffer">The buffer to write the decoded audio data into. The data is in 32-bit floating point format.</param>
    /// <returns>An <see cref="AudioDecoderResult"/> indicating the number of frames read.</returns>
    AudioDecoderResult ReadFrames(byte[] buffer);

    /// <summary>
    /// Try to seeks audio stream to the specified position and returns <c>true</c> if successfully seeks,
    /// otherwise, <c>false</c>.
    /// </summary>
    /// <param name="position">Desired seek position.</param>
    /// <param name="error">An error message while seeking audio stream.</param>
    /// <returns><c>true</c> if successfully seeks, otherwise, <c>false</c>.</returns>
    bool TrySeek(TimeSpan position, out string error);
}
