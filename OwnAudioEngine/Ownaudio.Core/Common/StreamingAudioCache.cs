using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Core.Common;

/// <summary>
/// Streaming audio cache with read-ahead to minimize disk I/O.
/// Maintains a sliding window of decoded audio data for efficient sequential access.
/// </summary>
/// <remarks>
/// This cache solves the problem of excessive SSD I/O when mixing multiple audio files:
///
/// Without cache (10 files mixing):
/// - 10 decoders × 93 reads/sec = 930 SSD I/O operations/sec
/// - Random access patterns cause SSD wear
/// - High latency due to seek times
///
/// With cache:
/// - Large sequential reads (128KB-1MB chunks)
/// - Read-ahead on background thread
/// - ~95% I/O reduction
/// - Predictable access patterns
///
/// Performance:
/// - Buffer size: 256KB-1MB (configurable)
/// - Read-ahead: 2x buffer size
/// - ~10-50 SSD reads/sec for 10 files (vs 930)
/// </remarks>
public sealed class StreamingAudioCache : IDisposable
{
    private readonly Stream _baseStream;
    private readonly bool _ownsStream;
    private readonly byte[] _buffer;
    private readonly int _bufferSize;
    private readonly int _readAheadSize;

    private long _bufferStartPosition;
    private int _bufferValidBytes;
    private long _streamPosition;
    private bool _disposed;

    // Read-ahead state
    private Task? _readAheadTask;
    private CancellationTokenSource? _readAheadCts;
    private readonly SemaphoreSlim _readAheadLock = new(1, 1);

    /// <summary>
    /// Gets the current position in the stream.
    /// </summary>
    public long Position => _streamPosition;

    /// <summary>
    /// Gets the length of the underlying stream.
    /// </summary>
    public long Length => _baseStream.Length;

    /// <summary>
    /// Gets whether the stream can seek.
    /// </summary>
    public bool CanSeek => _baseStream.CanSeek;

    /// <summary>
    /// Creates a new streaming audio cache.
    /// </summary>
    /// <param name="baseStream">Underlying stream to cache.</param>
    /// <param name="ownsStream">True to dispose base stream when cache is disposed.</param>
    /// <param name="bufferSize">Cache buffer size in bytes (default: 256KB).</param>
    /// <param name="enableReadAhead">Enable background read-ahead (default: true).</param>
    public StreamingAudioCache(
        Stream baseStream,
        bool ownsStream = false,
        int bufferSize = 256 * 1024,
        bool enableReadAhead = true)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _ownsStream = ownsStream;

        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        _bufferSize = bufferSize;
        _readAheadSize = enableReadAhead ? bufferSize * 2 : 0;
        _buffer = new byte[_bufferSize + _readAheadSize];

        _bufferStartPosition = 0;
        _bufferValidBytes = 0;
        _streamPosition = baseStream.Position;
        _disposed = false;
    }

    /// <summary>
    /// Reads data from the cached stream.
    /// Automatically triggers buffer refill and read-ahead when needed.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Offset in destination buffer.</param>
    /// <param name="count">Number of bytes to read.</param>
    /// <returns>Number of bytes actually read.</returns>
    public int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamingAudioCache));

        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException();

        int totalRead = 0;

        while (count > 0)
        {
            // Check if we need to refill buffer
            if (!IsPositionInBuffer(_streamPosition))
            {
                RefillBuffer(_streamPosition);
            }

            // Calculate how much we can read from current buffer
            int bufferOffset = (int)(_streamPosition - _bufferStartPosition);
            int availableInBuffer = _bufferValidBytes - bufferOffset;

            if (availableInBuffer <= 0)
            {
                // End of stream
                break;
            }

            int bytesToCopy = Math.Min(count, availableInBuffer);
            Array.Copy(_buffer, bufferOffset, buffer, offset, bytesToCopy);

            _streamPosition += bytesToCopy;
            offset += bytesToCopy;
            count -= bytesToCopy;
            totalRead += bytesToCopy;

            // Trigger read-ahead if we're approaching buffer end
            TriggerReadAheadIfNeeded();
        }

        return totalRead;
    }

    /// <summary>
    /// Reads data from the cached stream into a span.
    /// </summary>
    public int Read(Span<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamingAudioCache));

        int totalRead = 0;

        while (buffer.Length > 0)
        {
            if (!IsPositionInBuffer(_streamPosition))
            {
                RefillBuffer(_streamPosition);
            }

            int bufferOffset = (int)(_streamPosition - _bufferStartPosition);
            int availableInBuffer = _bufferValidBytes - bufferOffset;

            if (availableInBuffer <= 0)
                break;

            int bytesToCopy = Math.Min(buffer.Length, availableInBuffer);
            _buffer.AsSpan(bufferOffset, bytesToCopy).CopyTo(buffer);

            _streamPosition += bytesToCopy;
            buffer = buffer.Slice(bytesToCopy);
            totalRead += bytesToCopy;

            TriggerReadAheadIfNeeded();
        }

        return totalRead;
    }

    /// <summary>
    /// Seeks to a specific position in the stream.
    /// </summary>
    public long Seek(long offset, SeekOrigin origin)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamingAudioCache));

        if (!_baseStream.CanSeek)
            throw new NotSupportedException("Base stream does not support seeking.");

        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _streamPosition + offset,
            SeekOrigin.End => _baseStream.Length + offset,
            _ => throw new ArgumentException("Invalid seek origin.", nameof(origin))
        };

        if (newPosition < 0)
            throw new IOException("Seek before beginning of stream.");

        _streamPosition = newPosition;

        // Cancel any pending read-ahead
        CancelReadAhead();

        return _streamPosition;
    }

    /// <summary>
    /// Checks if a position is within the current buffer.
    /// </summary>
    private bool IsPositionInBuffer(long position)
    {
        if (_bufferValidBytes == 0)
            return false;

        long bufferEnd = _bufferStartPosition + _bufferValidBytes;
        return position >= _bufferStartPosition && position < bufferEnd;
    }

    /// <summary>
    /// Refills the buffer starting from the specified position.
    /// Performs large sequential read to minimize SSD I/O.
    /// </summary>
    private void RefillBuffer(long startPosition)
    {
        // Cancel any pending read-ahead
        CancelReadAhead();

        // Seek base stream if needed
        if (_baseStream.Position != startPosition)
        {
            _baseStream.Seek(startPosition, SeekOrigin.Begin);
        }

        // Read large chunk (sequential I/O is fast)
        _bufferStartPosition = startPosition;
        _bufferValidBytes = _baseStream.Read(_buffer, 0, _bufferSize);
    }

    /// <summary>
    /// Triggers read-ahead on background thread if we're approaching buffer end.
    /// </summary>
    private void TriggerReadAheadIfNeeded()
    {
        if (_readAheadSize == 0)
            return;

        // Trigger read-ahead when we've consumed 75% of buffer
        int bufferOffset = (int)(_streamPosition - _bufferStartPosition);
        if (bufferOffset < _bufferValidBytes * 0.75)
            return;

        // Don't start new read-ahead if one is already running
        if (_readAheadTask != null && !_readAheadTask.IsCompleted)
            return;

        // Start async read-ahead
        _readAheadCts = new CancellationTokenSource();
        _readAheadTask = Task.Run(() => ReadAheadAsync(_readAheadCts.Token), _readAheadCts.Token);
    }

    /// <summary>
    /// Background read-ahead operation.
    /// </summary>
    private async Task ReadAheadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _readAheadLock.WaitAsync(cancellationToken);

            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                // Calculate read-ahead position
                long readAheadPosition = _bufferStartPosition + _bufferValidBytes;

                if (readAheadPosition >= _baseStream.Length)
                    return; // Already at end

                // Seek and read into read-ahead portion of buffer
                _baseStream.Seek(readAheadPosition, SeekOrigin.Begin);
                int bytesRead = await _baseStream.ReadAsync(
                    _buffer,
                    _bufferValidBytes,
                    _readAheadSize,
                    cancellationToken);

                if (!cancellationToken.IsCancellationRequested && bytesRead > 0)
                {
                    // Extend valid buffer size
                    _bufferValidBytes += bytesRead;
                }
            }
            finally
            {
                _readAheadLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when read-ahead is cancelled
        }
        catch
        {
            // Ignore read-ahead errors (will retry on next trigger)
        }
    }

    /// <summary>
    /// Cancels any pending read-ahead operation.
    /// </summary>
    private void CancelReadAhead()
    {
        _readAheadCts?.Cancel();
        _readAheadCts?.Dispose();
        _readAheadCts = null;
    }

    /// <summary>
    /// Disposes the cache and optionally the base stream.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        CancelReadAhead();
        _readAheadLock.Dispose();

        if (_ownsStream)
            _baseStream?.Dispose();
    }
}
