using System.Collections.Concurrent;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.BufferManagement;

/// <summary>
/// Keeps a stash of float buffers around so the mix path doesn't hand work to the GC.
/// Backed by a ConcurrentBag, so any thread may rent and return.
/// </summary>
public sealed class AudioBufferPool
{
    private readonly ConcurrentBag<float[]> _pool;
    private readonly int _bufferSize;
    private readonly int _maxPoolSize;
    private int _currentPoolSize;

    /// <summary>
    /// Length of every buffer this pool deals in, in samples.
    /// </summary>
    public int BufferSize => _bufferSize;

    /// <summary>
    /// Buffers sitting idle in the pool right now.
    /// </summary>
    public int PoolSize => _currentPoolSize;

    /// <summary>
    /// initialPoolSize buffers get allocated up front, and we never keep more
    /// than maxPoolSize of them around.
    /// </summary>
    /// <param name="bufferSize">Size of one buffer in samples.</param>
    /// <param name="initialPoolSize"></param>
    /// <param name="maxPoolSize"></param>
    public AudioBufferPool(int bufferSize, int initialPoolSize = 4, int maxPoolSize = 16)
    {
        if (bufferSize <= 0)
            throw new AudioException("AudioBufferPool ERROR: ", new ArgumentException("Buffer size must be greater than zero.", nameof(bufferSize)));
        if (maxPoolSize < initialPoolSize || initialPoolSize < 0)
            throw new AudioException("AudioBufferPool ERROR: ", new ArgumentException("Bad pool sizes, need 0 <= initial <= max.", nameof(maxPoolSize)));

        _bufferSize = bufferSize;
        _maxPoolSize = maxPoolSize;
        _pool = new ConcurrentBag<float[]>();

        for (int i = 0; i < initialPoolSize; i++)
        {
            _pool.Add(new float[bufferSize]);
            _currentPoolSize++;
        }
    }

    /// <summary>
    /// Grabs a buffer, allocating a fresh one when the pool ran dry.
    /// </summary>
    public float[] Rent()
    {
        if (_pool.TryTake(out float[]? buffer))
        {
            Interlocked.Decrement(ref _currentPoolSize);
            return buffer;
        }

        return new float[_bufferSize];
    }

    /// <summary>
    /// Hands a buffer back. Wrong-sized ones are dropped on the floor rather than
    /// poisoning the pool.
    /// </summary>
    public void Return(float[] buffer)
    {
        if (buffer == null || buffer.Length != _bufferSize) return;
        if (_currentPoolSize >= _maxPoolSize) return;

        Array.Clear(buffer, 0, buffer.Length);
        _pool.Add(buffer);
        Interlocked.Increment(ref _currentPoolSize);
    }

    /// <summary>
    /// Throws away everything we're holding.
    /// </summary>
    public void Clear()
    {
        while (_pool.TryTake(out _))
            Interlocked.Decrement(ref _currentPoolSize);
    }

    /// <summary>
    /// Snapshot of the pool, mostly for diagnostics.
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
    /// Plain value snapshot of the pool state.
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
