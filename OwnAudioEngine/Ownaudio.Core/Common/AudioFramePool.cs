using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Ownaudio.Core.Common;

/// <summary>
/// Zero-allocation pool for AudioFrame objects and their byte buffers.
/// Eliminates per-frame allocations in decoder hot paths.
/// </summary>
/// <remarks>
/// Thread-safe pool implementation for reusing AudioFrame instances and their data buffers.
/// Reduces GC pressure by reusing pre-allocated buffers across decode operations.
///
/// Usage pattern:
/// 1. Rent() - Get a pooled frame with buffer
/// 2. Use the frame
/// 3. Return() - Return frame to pool for reuse
/// </remarks>
public sealed class AudioFramePool
{
    private readonly ConcurrentBag<PooledAudioFrame> _frames;
    private readonly int _bufferSize;
    private readonly int _maxPoolSize;
    private int _currentSize;

    /// <summary>
    /// Creates a new AudioFrame pool.
    /// </summary>
    /// <param name="bufferSize">Size of each frame buffer in bytes.</param>
    /// <param name="initialPoolSize">Number of frames to pre-allocate.</param>
    /// <param name="maxPoolSize">Maximum number of frames to keep in pool (0 = unlimited).</param>
    public AudioFramePool(int bufferSize, int initialPoolSize = 4, int maxPoolSize = 16)
    {
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        _bufferSize = bufferSize;
        _maxPoolSize = maxPoolSize;
        _frames = new ConcurrentBag<PooledAudioFrame>();
        _currentSize = 0;

        // Pre-allocate initial frames
        for (int i = 0; i < initialPoolSize; i++)
        {
            _frames.Add(new PooledAudioFrame(new byte[bufferSize]));
            _currentSize++;
        }
    }

    /// <summary>
    /// Rents a pooled AudioFrame with pre-allocated buffer.
    /// </summary>
    /// <param name="presentationTime">Presentation time for the frame.</param>
    /// <param name="dataLength">Actual data length to use (must be &lt;= buffer size).</param>
    /// <returns>A pooled AudioFrame ready for use.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledAudioFrame Rent(double presentationTime, int dataLength)
    {
        if (dataLength > _bufferSize)
            throw new ArgumentException($"Data length {dataLength} exceeds buffer size {_bufferSize}");

        if (_frames.TryTake(out PooledAudioFrame frame))
        {
            // Reuse pooled frame
            frame.Reset(presentationTime, dataLength);
            return frame;
        }

        // Pool empty, create new frame
        return new PooledAudioFrame(new byte[_bufferSize], presentationTime, dataLength);
    }

    /// <summary>
    /// Returns a frame to the pool for reuse.
    /// </summary>
    /// <param name="frame">Frame to return (can be null).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(PooledAudioFrame frame)
    {
        if (frame == null)
            return;

        // Check buffer size matches pool
        if (frame.BufferCapacity != _bufferSize)
            return; // Discard frames with wrong buffer size

        // Check max size limit
        if (_maxPoolSize > 0 && _currentSize >= _maxPoolSize)
            return; // Discard excess frames

        _frames.Add(frame);
        _currentSize++;
    }

    /// <summary>
    /// Gets the buffer size for this pool.
    /// </summary>
    public int BufferSize => _bufferSize;

    /// <summary>
    /// Clears the pool and releases all pooled frames.
    /// </summary>
    public void Clear()
    {
        while (_frames.TryTake(out _))
        {
            _currentSize--;
        }
    }
}

/// <summary>
/// Pooled version of AudioFrame that reuses the same byte buffer.
/// Provides zero-allocation frame data via spans.
/// </summary>
public sealed class PooledAudioFrame
{
    private readonly byte[] _buffer;
    private double _presentationTime;
    private int _dataLength;

    /// <summary>
    /// Gets frame presentation time in milliseconds.
    /// </summary>
    public double PresentationTime => _presentationTime;

    /// <summary>
    /// Gets the active data span (length = DataLength, not full buffer).
    /// Use this for zero-copy access to frame data.
    /// </summary>
    public Span<byte> DataSpan => _buffer.AsSpan(0, _dataLength);

    /// <summary>
    /// Gets the full buffer span for writing.
    /// </summary>
    public Span<byte> BufferSpan => _buffer.AsSpan();

    /// <summary>
    /// Gets actual data length in bytes.
    /// </summary>
    public int DataLength => _dataLength;

    /// <summary>
    /// Gets total buffer capacity in bytes.
    /// </summary>
    public int BufferCapacity => _buffer.Length;

    /// <summary>
    /// Converts to standard AudioFrame (allocates new byte array).
    /// Use this only when AudioFrame is required by API.
    /// </summary>
    public AudioFrame ToAudioFrame()
    {
        byte[] data = new byte[_dataLength];
        _buffer.AsSpan(0, _dataLength).CopyTo(data);
        return new AudioFrame(_presentationTime, data);
    }

    internal PooledAudioFrame(byte[] buffer, double presentationTime = 0.0, int dataLength = 0)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _presentationTime = presentationTime;
        _dataLength = dataLength;
    }

    internal void Reset(double presentationTime, int dataLength)
    {
        if (dataLength > _buffer.Length)
            throw new ArgumentException($"Data length {dataLength} exceeds buffer capacity {_buffer.Length}");

        _presentationTime = presentationTime;
        _dataLength = dataLength;
    }
}
