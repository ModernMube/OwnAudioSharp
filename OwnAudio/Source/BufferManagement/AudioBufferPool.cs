using System.Collections.Concurrent;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.BufferManagement;

/// <summary>
/// Object pool for audio buffers to minimize GC allocations.
/// Thread-safe implementation using ConcurrentBag.
/// </summary>
public sealed class AudioBufferPool
{
    private readonly ConcurrentBag<float[]> _pool;
    private readonly int _bufferSize;
    private readonly int _maxPoolSize;
    private int _currentPoolSize;

    /// <summary>
    /// Gets the buffer size in samples.
    /// </summary>
    public int BufferSize => _bufferSize;

    /// <summary>
    /// Gets the current number of buffers in the pool.
    /// </summary>
    public int PoolSize => _currentPoolSize;

    /// <summary>
    /// Initializes a new instance of the AudioBufferPool class.
    /// </summary>
    /// <param name="bufferSize">The size of each buffer in samples.</param>
    /// <param name="initialPoolSize">The initial number of buffers to pre-allocate.</param>
    /// <param name="maxPoolSize">The maximum number of buffers to keep in the pool.</param>
    public AudioBufferPool(int bufferSize, int initialPoolSize = 4, int maxPoolSize = 16)
    {
        if (bufferSize <= 0)
            throw new AudioException("AudioBufferPool ERROR: ", new ArgumentException("Buffer size must be greater than zero.", nameof(bufferSize)));
        if (initialPoolSize < 0)
            throw new AudioException("AudioBufferPool ERROR: ", new ArgumentException("Initial pool size cannot be negative.", nameof(initialPoolSize)));
        if (maxPoolSize < initialPoolSize)
            throw new AudioException("AudioBufferPool ERROR: ", new ArgumentException("Max pool size must be >= initial pool size.", nameof(maxPoolSize)));

        _bufferSize = bufferSize;
        _maxPoolSize = maxPoolSize;
        _pool = new ConcurrentBag<float[]>();
        _currentPoolSize = 0;

        // Pre-allocate initial buffers
        for (int i = 0; i < initialPoolSize; i++)
        {
            _pool.Add(new float[bufferSize]);
            Interlocked.Increment(ref _currentPoolSize);
        }
    }

    /// <summary>
    /// Rents a buffer from the pool. If the pool is empty, allocates a new buffer.
    /// </summary>
    /// <returns>A buffer with at least BufferSize capacity.</returns>
    public float[] Rent()
    {
        if (_pool.TryTake(out float[]? buffer))
        {
            Interlocked.Decrement(ref _currentPoolSize);
            return buffer;
        }

        // Pool is empty, allocate new buffer
        return new float[_bufferSize];
    }

    /// <summary>
    /// Returns a buffer to the pool for reuse.
    /// </summary>
    /// <param name="buffer">The buffer to return.</param>
    public void Return(float[] buffer)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        if (buffer.Length != _bufferSize)
            throw new ArgumentException($"Buffer size mismatch. Expected {_bufferSize}, got {buffer.Length}.", nameof(buffer));

        // Clear the buffer before returning it
        Array.Clear(buffer, 0, buffer.Length);

        // Atomically increment first, then check if we should keep the buffer
        // This prevents race condition where multiple threads could exceed maxPoolSize
        int newSize = Interlocked.Increment(ref _currentPoolSize);

        if (newSize <= _maxPoolSize)
        {
            // We're within the limit, add to pool
            _pool.Add(buffer);
        }
        else
        {
            // We exceeded the limit - undo the increment and discard the buffer
            Interlocked.Decrement(ref _currentPoolSize);
            // Buffer will be garbage collected
        }
    }

    /// <summary>
    /// Clears all buffers from the pool.
    /// </summary>
    public void Clear()
    {
        while (_pool.TryTake(out _))
        {
            Interlocked.Decrement(ref _currentPoolSize);
        }
    }

    /// <summary>
    /// Gets statistics about the pool.
    /// </summary>
    public PoolStatistics GetStatistics()
    {
        return new PoolStatistics
        {
            BufferSize = _bufferSize,
            CurrentPoolSize = _currentPoolSize,
            MaxPoolSize = _maxPoolSize
        };
    }

    /// <summary>
    /// Represents statistics about the buffer pool.
    /// </summary>
    public struct PoolStatistics
    {
        public int BufferSize { get; init; }
        public int CurrentPoolSize { get; init; }
        public int MaxPoolSize { get; init; }

        public override readonly string ToString()
        {
            return $"BufferPool: {CurrentPoolSize}/{MaxPoolSize} buffers ({BufferSize} samples each)";
        }
    }
}
