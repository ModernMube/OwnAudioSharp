using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Ownaudio.Core.Common;

/// <summary>
/// Buffered stream wrapper optimized for audio decoding workloads.
/// Reduces syscalls by batching reads and maintaining internal buffer.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> NOT thread-safe. Should only be used by a single thread.</para>
/// <para><b>GC Behavior:</b> Zero-allocation during reads (pre-allocated buffer).</para>
/// <para><b>Performance:</b> Significantly reduces I/O syscalls compared to unbuffered reads.</para>
/// </remarks>
public sealed class OptimizedAudioStream : Stream
{
    private readonly Stream _baseStream;
    private readonly byte[] _buffer;
    private readonly bool _ownsBaseStream;
    private int _bufferPosition;
    private int _bufferLength;
    private long _position;
    private bool _disposed;

    // Default buffer size: 64KB (good balance for audio files)
    private const int DefaultBufferSize = 65536;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizedAudioStream"/> class.
    /// </summary>
    /// <param name="baseStream">The underlying stream to wrap.</param>
    /// <param name="bufferSize">Size of the internal buffer in bytes (default: 64KB).</param>
    /// <param name="ownsBaseStream">If true, disposes the base stream when this stream is disposed.</param>
    public OptimizedAudioStream(Stream baseStream, int bufferSize = DefaultBufferSize, bool ownsBaseStream = false)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _ownsBaseStream = ownsBaseStream;

        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize), "Buffer size must be positive");

        _buffer = new byte[bufferSize];
        _bufferPosition = 0;
        _bufferLength = 0;
        _position = baseStream.CanSeek ? baseStream.Position : 0;
        _disposed = false;
    }

    /// <inheritdoc/>
    public override bool CanRead => _baseStream.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => _baseStream.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => false; // Read-only stream

    /// <inheritdoc/>
    public override long Length => _baseStream.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadCore(new Span<byte>(buffer, offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadCore(buffer);
    }

    /// <summary>
    /// Core read implementation using Span for zero-allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadCore(Span<byte> destination)
    {
        int totalBytesRead = 0;

        while (destination.Length > 0)
        {
            // If buffer is empty, refill it
            if (_bufferPosition >= _bufferLength)
            {
                if (!RefillBuffer())
                {
                    // No more data available
                    break;
                }
            }

            // Calculate how many bytes we can copy from buffer
            int availableInBuffer = _bufferLength - _bufferPosition;
            int bytesToCopy = Math.Min(availableInBuffer, destination.Length);

            // Copy from buffer to destination
            _buffer.AsSpan(_bufferPosition, bytesToCopy).CopyTo(destination);

            _bufferPosition += bytesToCopy;
            _position += bytesToCopy;
            totalBytesRead += bytesToCopy;

            destination = destination.Slice(bytesToCopy);
        }

        return totalBytesRead;
    }

    /// <summary>
    /// Refills the internal buffer from the base stream.
    /// </summary>
    /// <returns>True if any data was read, false if end of stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RefillBuffer()
    {
        _bufferPosition = 0;
        _bufferLength = _baseStream.Read(_buffer, 0, _buffer.Length);
        return _bufferLength > 0;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!CanSeek)
            throw new NotSupportedException("Stream does not support seeking");

        // Calculate target position
        long targetPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
        };

        if (targetPosition < 0)
            throw new IOException("Cannot seek before beginning of stream");

        // Check if target is within current buffer
        long bufferStart = _position - _bufferPosition;
        long bufferEnd = bufferStart + _bufferLength;

        if (targetPosition >= bufferStart && targetPosition < bufferEnd)
        {
            // Seek within buffer (fast path)
            _bufferPosition = (int)(targetPosition - bufferStart);
            _position = targetPosition;
        }
        else
        {
            // Seek outside buffer - need to seek base stream and invalidate buffer
            _baseStream.Seek(targetPosition, SeekOrigin.Begin);
            _bufferPosition = 0;
            _bufferLength = 0;
            _position = targetPosition;
        }

        return _position;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // Read-only stream - nothing to flush
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        throw new NotSupportedException("Cannot set length on read-only stream");
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Cannot write to read-only stream");
    }

    /// <summary>
    /// Validates buffer arguments for Read operations.
    /// </summary>
    private static new void ValidateBufferArguments(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");

        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative");

        if (offset + count > buffer.Length)
            throw new ArgumentException("Offset and count exceed buffer length");
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            if (_ownsBaseStream)
            {
                _baseStream.Dispose();
            }
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    /// <summary>
    /// Gets the size of the internal buffer.
    /// </summary>
    public int BufferSize => _buffer.Length;

    /// <summary>
    /// Gets the number of bytes currently buffered.
    /// </summary>
    public int BufferedBytes => _bufferLength - _bufferPosition;
}
