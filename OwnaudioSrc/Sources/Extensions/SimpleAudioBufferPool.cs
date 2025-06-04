using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ownaudio.Sources.Extensions
{
    /// <summary>
    /// Simplified buffer pool for audio processing - optimized for large buffers with minimal overhead.
    /// This pool manages float arrays in three size categories to reduce garbage collection pressure
    /// and improve performance in audio processing scenarios.
    /// </summary>
    public static class SimpleAudioBufferPool
    {
        /// <summary>
        /// Pool for small buffers (512-1024 elements).
        /// </summary>
        private static readonly ConcurrentQueue<float[]> _smallBuffers = new();

        /// <summary>
        /// Pool for medium buffers (1024-2048 elements).
        /// </summary>
        private static readonly ConcurrentQueue<float[]> _mediumBuffers = new();

        /// <summary>
        /// Pool for large buffers (2048+ elements).
        /// </summary>
        private static readonly ConcurrentQueue<float[]> _largeBuffers = new();

        /// <summary>
        /// Current count of small buffers in the pool.
        /// </summary>
        private static int _smallCount = 0;

        /// <summary>
        /// Current count of medium buffers in the pool.
        /// </summary>
        private static int _mediumCount = 0;

        /// <summary>
        /// Current count of large buffers in the pool.
        /// </summary>
        private static int _largeCount = 0;

        /// <summary>
        /// Maximum number of buffers to keep in each pool category to limit memory usage.
        /// </summary>
        private const int MAX_POOL_SIZE = 10;

        /// <summary>
        /// Rents a buffer from the pool or creates a new one if none are available.
        /// For small buffers (≤256 elements), always creates a new buffer as simple allocation is faster.
        /// For larger buffers, attempts to reuse pooled buffers to reduce GC pressure.
        /// </summary>
        /// <param name="size">The minimum required size of the buffer.</param>
        /// <returns>A float array that is at least the requested size. The returned buffer may be larger than requested for better cache performance.</returns>
        public static float[] Rent(int size)
        {
            // For small buffers, don't use pooling - simple allocation is faster
            if (size <= 256)
                return new float[size];

            if (size <= 1024)
            {
                if (_smallBuffers.TryDequeue(out var buffer))
                {
                    Interlocked.Decrement(ref _smallCount);
                    FastClear(buffer, size); // Clear only the needed portion
                    return buffer;
                }
                return new float[1024]; // Fixed size for better cache performance
            }
            else if (size <= 2048)
            {
                if (_mediumBuffers.TryDequeue(out var buffer))
                {
                    Interlocked.Decrement(ref _mediumCount);
                    FastClear(buffer, size);
                    return buffer;
                }
                return new float[2048];
            }
            else
            {
                if (_largeBuffers.TryDequeue(out var buffer) && buffer.Length >= size)
                {
                    Interlocked.Decrement(ref _largeCount);
                    FastClear(buffer, size);
                    return buffer;
                }
                return new float[size];
            }
        }

        /// <summary>
        /// Returns a buffer to the pool for reuse if there's space available.
        /// Buffers are only pooled if they match the expected sizes and the pool isn't full.
        /// Small buffers and excess buffers are left for garbage collection.
        /// </summary>
        /// <param name="buffer">The buffer to return to the pool. Can be null.</param>
        public static void Return(float[] buffer)
        {
            if (buffer == null) return;

            int size = buffer.Length;

            if (size == 1024 && _smallCount < MAX_POOL_SIZE)
            {
                _smallBuffers.Enqueue(buffer);
                Interlocked.Increment(ref _smallCount);
            }
            else if (size == 2048 && _mediumCount < MAX_POOL_SIZE)
            {
                _mediumBuffers.Enqueue(buffer);
                Interlocked.Increment(ref _mediumCount);
            }
            else if (size > 2048 && _largeCount < MAX_POOL_SIZE)
            {
                _largeBuffers.Enqueue(buffer);
                Interlocked.Increment(ref _largeCount);
            }
            // Small buffers and buffers when pool is full are left for GC
        }

        /// <summary>
        /// Clears all buffers from all pools and resets counters.
        /// This method can be used to free up memory when the pool is no longer needed.
        /// </summary>
        public static void Clear()
        {
            while (_smallBuffers.TryDequeue(out _)) { }
            while (_mediumBuffers.TryDequeue(out _)) { }
            while (_largeBuffers.TryDequeue(out _)) { }
            _smallCount = _mediumCount = _largeCount = 0;
        }

        /// <summary>
        /// Efficiently clears a float array buffer using optimized methods based on buffer size.
        /// </summary>
        /// <param name="buffer">The float array buffer to clear.</param>
        /// <param name="length">The number of elements to clear from the start of the buffer.</param>
        /// <remarks>
        /// This method uses size-based optimization:
        /// - For buffers ≤1024 elements: Uses Span.Clear() which is optimized for smaller buffers
        /// - For larger buffers: Uses Array.Clear() which is more efficient for larger memory blocks
        /// This approach provides better performance across different buffer sizes.
        /// </remarks>
        private static void FastClear(float[] buffer, int length)
        {
            if (length <= 1024)
                buffer.AsSpan(0, length).Clear(); // Span.Clear - optimalized
            else
                Array.Clear(buffer, 0, buffer.Length); // Array.Clear - nagyobb buffer-eknél
        }
    }
}