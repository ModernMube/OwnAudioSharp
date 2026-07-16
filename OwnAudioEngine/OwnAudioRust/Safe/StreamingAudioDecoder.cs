using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Native.RustAudio.Structs;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;
using Ownaudio.Safe.Validation;

namespace Ownaudio.Safe;

/// <summary>
/// Streaming file decoder over the Rust engine. A native prefetch thread fills a small
/// lock-free ring buffer, so RAM tracks the prefetch size, not the file. Not thread-safe
/// per instance; Read is RT-safe. Symphonia backend (wav/mp3/flac/ogg/aac/aiff).
/// </summary>
public sealed class StreamingAudioDecoder : IDisposable
{
    // native uses u64::MAX when it can't tell the duration
    private const ulong UnknownDuration = ulong.MaxValue;

    private readonly StreamingDecoderHandle _handle;
    private bool _disposed;

    // metadata grabbed at open time
    public AudioStreamInfo StreamInfo { get; }

    // true once decoded to the end and the prefetch buffer ran dry
    public bool IsEndOfStream
    {
        get
        {
            Guard.NotDisposed(_disposed, nameof(StreamingAudioDecoder));
            int code = OwnAudioNative.ownaudio_v1_decoder_is_eof(_handle.DangerousGetHandle(), out bool eof);
            ErrorCodeMapper.ThrowIfError(code, nameof(IsEndOfStream));
            return eof;
        }
    }

    // targetSampleRate/targetChannels = 0 means keep the source; prefetchSeconds sizes the ring
    public StreamingAudioDecoder(string filePath, int targetSampleRate = 0, int targetChannels = 0, float prefetchSeconds = 2.0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (targetSampleRate != 0) Guard.InRange(targetSampleRate, 8_000, 192_000, nameof(targetSampleRate));
        if (targetChannels != 0) Guard.InRange(targetChannels, 1, 256, nameof(targetChannels));

        int openCode = OwnAudioNative.ownaudio_v1_decoder_open(
            filePath, (uint)targetSampleRate, (uint)targetChannels, prefetchSeconds, out IntPtr rawHandle);
        ErrorCodeMapper.ThrowIfError(openCode, nameof(StreamingAudioDecoder));

        _handle = new StreamingDecoderHandle();
        Marshal.InitHandle(_handle, rawHandle);

        int infoCode = OwnAudioNative.ownaudio_v1_decoder_get_stream_info(rawHandle, out NativeAudioStreamInfo native);
        if (infoCode != (int)NativeErrorCode.Success)
        {
            _handle.Dispose();
            ErrorCodeMapper.ThrowIfError(infoCode, nameof(StreamingAudioDecoder));
        }

        bool known = native.DurationMs != UnknownDuration;
        StreamInfo = new AudioStreamInfo(
            channels: (int)native.Channels,
            sampleRate: (int)native.SampleRate,
            duration: known ? TimeSpan.FromMilliseconds(native.DurationMs) : TimeSpan.Zero,
            bitDepth: (int)native.BitDepth,
            hasKnownDuration: known);
    }

    // zero-alloc hot path; returns samples written, short read = EOF or a prefetch underrun
    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length);
        return Read(buffer.AsSpan(offset, count));
    }

    // span overload of the read hot path
    public int Read(Span<float> destination)
    {
        Guard.NotDisposed(_disposed, nameof(StreamingAudioDecoder));
        if (destination.IsEmpty) return 0;

        int code = OwnAudioNative.ownaudio_v1_decoder_read(
            _handle.DangerousGetHandle(),
            ref MemoryMarshal.GetReference(destination),
            (nuint)destination.Length,
            out nuint written);
        ErrorCodeMapper.ThrowIfError(code, nameof(Read));
        return (int)written;
    }

    // non-blocking; the prefetch thread does the actual seek. framePosition = output frame
    public void Seek(long framePosition)
    {
        Guard.NotDisposed(_disposed, nameof(StreamingAudioDecoder));
        ArgumentOutOfRangeException.ThrowIfNegative(framePosition);

        int code = OwnAudioNative.ownaudio_v1_decoder_seek(_handle.DangerousGetHandle(), (ulong)framePosition);
        ErrorCodeMapper.ThrowIfError(code, nameof(Seek));
    }

    // seek by time
    public void Seek(TimeSpan position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        Seek((long)(position.TotalSeconds * StreamInfo.SampleRate));
    }

    // kills the native decoder + joins the prefetch thread; idempotent
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}
