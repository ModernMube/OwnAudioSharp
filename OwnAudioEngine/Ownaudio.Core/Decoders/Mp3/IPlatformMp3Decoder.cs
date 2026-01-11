using System;
using System.IO;

namespace Ownaudio.Decoders.Mp3;

/// <summary>
/// Platform-specific MP3 decoder interface.
/// Abstracts platform differences (Windows Media Foundation, macOS Core Audio, managed fallback).
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> NOT thread-safe. Single thread use only.</para>
/// <para><b>GC Behavior:</b> Zero-allocation during DecodeFrame after initialization.</para>
/// <para><b>Implementations:</b> WindowsMFMp3Decoder, CoreAudioMp3Decoder, ManagedMp3Decoder.</para>
/// </remarks>
public interface IPlatformMp3Decoder : IDisposable
{
    /// <summary>
    /// Initializes the decoder with a file path.
    /// </summary>
    /// <param name="filePath">Path to MP3 file.</param>
    /// <param name="targetSampleRate">Target sample rate (0 = use source rate).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels).</param>
    void InitializeFromFile(string filePath, int targetSampleRate, int targetChannels);

    /// <summary>
    /// Initializes the decoder with a stream.
    /// </summary>
    /// <param name="stream">Stream containing MP3 data. Must support seeking and reading.</param>
    /// <param name="targetSampleRate">Target sample rate (0 = use source rate).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels).</param>
    void InitializeFromStream(Stream stream, int targetSampleRate, int targetChannels);

    /// <summary>
    /// Gets information about the audio stream.
    /// </summary>
    /// <returns>Audio stream information.</returns>
    AudioStreamInfo GetStreamInfo();

    /// <summary>
    /// Decodes a single frame of audio data.
    /// </summary>
    /// <param name="outputBuffer">Buffer to write decoded data (Float32 interleaved).</param>
    /// <param name="pts">Presentation timestamp in milliseconds (output).</param>
    /// <returns>Number of bytes written to output buffer, 0 if EOF, -1 if error.</returns>
    /// <remarks>
    /// ZERO-ALLOCATION: Writes directly to provided buffer.
    /// Returns number of BYTES written (not samples).
    /// </remarks>
    int DecodeFrame(Span<byte> outputBuffer, out double pts);

    /// <summary>
    /// Seeks to the specified sample position.
    /// </summary>
    /// <param name="samplePosition">Target position in samples (per channel).</param>
    /// <returns>True if seek succeeded, false otherwise.</returns>
    bool Seek(long samplePosition);

    /// <summary>
    /// Gets the current presentation timestamp in milliseconds.
    /// </summary>
    double CurrentPts { get; }

    /// <summary>
    /// Gets whether the decoder has reached end-of-file.
    /// </summary>
    bool IsEOF { get; }
}
