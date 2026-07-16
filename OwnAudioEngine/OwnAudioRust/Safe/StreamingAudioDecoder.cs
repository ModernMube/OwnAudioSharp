using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Native.RustAudio.Structs;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;
using Ownaudio.Safe.Validation;

namespace Ownaudio.Safe;

/// <summary>
/// Memory-efficient streaming audio file decoder backed by the native Rust engine.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated native prefetch thread decodes the file incrementally into a small
/// lock-free ring buffer, so memory usage is bounded by the prefetch size rather
/// than the file size. This makes large multitrack playback affordable.
/// </para>
/// <para>
/// Decoding uses the pure-Rust Symphonia backend (WAV, MP3, FLAC, OGG/Vorbis,
/// AAC/M4A, AIFF), which is always available without any external dependency.
/// FFmpeg-backed decoding is handled separately by the managed FFmpeg layer,
/// which is used only when FFmpeg is installed on the system.
/// </para>
/// <para>
/// <b>Thread safety:</b> a single instance must not be used concurrently from
/// multiple threads. <see cref="Read(float[], int, int)"/> is real-time safe.
/// </para>
/// </remarks>
public sealed class StreamingAudioDecoder : IDisposable
{
    #region Fields

    private const ulong UnknownDurationSentinel = ulong.MaxValue;

    private readonly StreamingDecoderHandle _handle;
    private bool _disposed;

    #endregion

    #region Properties

    /// <summary>Decoded output stream metadata, captured at open time.</summary>
    public AudioStreamInfo StreamInfo { get; }

    /// <summary>
    /// <see langword="true"/> once the file has been fully decoded and the
    /// prefetch buffer is drained.
    /// </summary>
    public bool IsEndOfStream
    {
        get
        {
            Guard.NotDisposed(_disposed, nameof(StreamingAudioDecoder));
            int code = OwnAudioNative.ownaudio_v1_decoder_is_eof(
                _handle.DangerousGetHandle(), out bool eof);
            ErrorCodeMapper.ThrowIfError(code, nameof(IsEndOfStream));
            return eof;
        }
    }

    #endregion

    #region Construction

    /// <summary>
    /// Opens an audio file for streaming decoding.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <param name="targetSampleRate">Output sample rate in Hz (0 = source rate).</param>
    /// <param name="targetChannels">Output channel count (0 = source channels).</param>
    /// <param name="prefetchSeconds">Prefetch buffer length in seconds (default 2.0).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="filePath"/> is null/blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a numeric argument is out of range.</exception>
    /// <exception cref="DecoderException">Thrown when the file cannot be opened or decoded.</exception>
    public StreamingAudioDecoder(
        string filePath,
        int targetSampleRate = 0,
        int targetChannels = 0,
        float prefetchSeconds = 2.0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (targetSampleRate != 0)
        {
            Guard.InRange(targetSampleRate, 8_000, 192_000, nameof(targetSampleRate));
        }
        if (targetChannels != 0)
        {
            Guard.InRange(targetChannels, 1, 256, nameof(targetChannels));
        }

        int openCode = OwnAudioNative.ownaudio_v1_decoder_open(
            filePath,
            (uint)targetSampleRate,
            (uint)targetChannels,
            prefetchSeconds,
            out IntPtr rawHandle);
        ErrorCodeMapper.ThrowIfError(openCode, nameof(StreamingAudioDecoder));

        _handle = new StreamingDecoderHandle();
        Marshal.InitHandle(_handle, rawHandle);

        int infoCode = OwnAudioNative.ownaudio_v1_decoder_get_stream_info(
            rawHandle, out NativeAudioStreamInfo native);
        if (infoCode != (int)NativeErrorCode.Success)
        {
            _handle.Dispose();
            ErrorCodeMapper.ThrowIfError(infoCode, nameof(StreamingAudioDecoder));
        }

        bool knownDuration = native.DurationMs != UnknownDurationSentinel;
        StreamInfo = new AudioStreamInfo(
            channels: (int)native.Channels,
            sampleRate: (int)native.SampleRate,
            duration: knownDuration ? TimeSpan.FromMilliseconds(native.DurationMs) : TimeSpan.Zero,
            bitDepth: (int)native.BitDepth,
            hasKnownDuration: knownDuration);
    }

    #endregion

    #region Reading

    /// <summary>
    /// Reads decoded interleaved <c>float</c> samples into <paramref name="buffer"/>.
    /// This is the zero-allocation hot path.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Zero-based start index in <paramref name="buffer"/>.</param>
    /// <param name="count">Maximum number of samples to read.</param>
    /// <returns>
    /// The number of samples actually written; a value smaller than
    /// <paramref name="count"/> indicates EOF or a transient prefetch underrun.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown when the decoder has been disposed.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="buffer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the range is invalid.</exception>
    /// <exception cref="DecoderException">Thrown when the native read fails.</exception>
    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length);

        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Reads decoded interleaved <c>float</c> samples into <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Destination span.</param>
    /// <returns>The number of samples actually written.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the decoder has been disposed.</exception>
    /// <exception cref="DecoderException">Thrown when the native read fails.</exception>
    public int Read(Span<float> destination)
    {
        Guard.NotDisposed(_disposed, nameof(StreamingAudioDecoder));
        if (destination.IsEmpty)
        {
            return 0;
        }

        int code = OwnAudioNative.ownaudio_v1_decoder_read(
            _handle.DangerousGetHandle(),
            ref MemoryMarshal.GetReference(destination),
            (nuint)destination.Length,
            out nuint written);
        ErrorCodeMapper.ThrowIfError(code, nameof(Read));

        return (int)written;
    }

    #endregion

    #region Seeking

    /// <summary>
    /// Requests a non-blocking seek to the given output sample-frame position.
    /// The prefetch thread performs the seek asynchronously.
    /// </summary>
    /// <param name="framePosition">Target output frame (zero-based).</param>
    /// <exception cref="ObjectDisposedException">Thrown when the decoder has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="framePosition"/> is negative.</exception>
    /// <exception cref="DecoderException">Thrown when the native seek fails.</exception>
    public void Seek(long framePosition)
    {
        Guard.NotDisposed(_disposed, nameof(StreamingAudioDecoder));
        ArgumentOutOfRangeException.ThrowIfNegative(framePosition);

        int code = OwnAudioNative.ownaudio_v1_decoder_seek(
            _handle.DangerousGetHandle(), (ulong)framePosition);
        ErrorCodeMapper.ThrowIfError(code, nameof(Seek));
    }

    /// <summary>
    /// Requests a non-blocking seek to the given time position.
    /// </summary>
    /// <param name="position">Target playback position.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the decoder has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="position"/> is negative.</exception>
    /// <exception cref="DecoderException">Thrown when the native seek fails.</exception>
    public void Seek(TimeSpan position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        long frame = (long)(position.TotalSeconds * StreamInfo.SampleRate);
        Seek(frame);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Destroys the native decoder, stopping and joining the prefetch thread.
    /// Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    #endregion
}
