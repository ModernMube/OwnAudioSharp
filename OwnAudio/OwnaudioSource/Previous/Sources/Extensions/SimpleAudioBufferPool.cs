using System;
using System.Collections.Concurrent;
using System.Threading;

namespace OwnaudioLegacy.Sources.Extensions
{
    /// <summary>
    /// Simplified buffer pool for audio processing - optimized for large buffers with minimal overhead.
    /// This pool manages float arrays in multiple size categories to reduce garbage collection pressure
    /// and improve performance in audio processing scenarios.
    /// </summary>
    public static class SimpleAudioBufferPool
    {
        /// <summary>
        /// Pool for 512-sample buffers.
        /// </summary>
        private static readonly ConcurrentQueue<float[]> _buffer512 = new();

        /// <summary>
        /// Pool for 1024-sample buffers.
        /// </summary>
        private static readonly ConcurrentQueue<float[]> _buffer1024 = new();

        /// <summary>
        /// Pool for 2048-sample buffers.
        /// </summary>
        private static readonly ConcurrentQueue<float[]> _buffer2048 = new();

        /// <summary>
        /// Pool for 4096-sample buffers.
        /// </summary>
        private static readonly ConcurrentQueue<float[]> _buffer4096 = new();

        /// <summary>
        /// Pool for 8192-sample buffers.
        /// </summary>
        private static readonly ConcurrentQueue<float[]> _buffer8192 = new();

        /// <summary>
        /// Pool for large buffers (>8192 elements).
        /// </summary>
        private static readonly ConcurrentQueue<float[]> _largeBuffers = new();

        // Counters for each pool
        private static int _count512 = 0;
        private static int _count1024 = 0;
        private static int _count2048 = 0;
        private static int _count4096 = 0;
        private static int _count8192 = 0;
        private static int _countLarge = 0;

        /// <summary>
        /// Maximum number of buffers to keep in each pool category to limit memory usage.
        /// </summary>
        private const int MAX_POOL_SIZE = 15;

        /// <summary>
        /// Rents a buffer from the pool or creates a new one if none are available.
        /// Uses size-based pooling for optimal performance across different buffer sizes.
        /// </summary>
        /// <param name="size">The minimum required size of the buffer.</param>
        /// <returns>A float array that is at least the requested size.</returns>
        public static float[] Rent(int size)
        {
            // For very small buffers, don't use pooling - allocation is faster
            if (size <= 256)
                return new float[size];

            // Select the appropriate pool based on size
            if (size <= 512)
            {
                if (_buffer512.TryDequeue(out var buffer))
                {
                    Interlocked.Decrement(ref _count512);
                    return buffer;
                }
                return new float[512];
            }
            else if (size <= 1024)
            {
                if (_buffer1024.TryDequeue(out var buffer))
                {
                    Interlocked.Decrement(ref _count1024);
                    return buffer;
                }
                return new float[1024];
            }
            else if (size <= 2048)
            {
                if (_buffer2048.TryDequeue(out var buffer))
                {
                    Interlocked.Decrement(ref _count2048);
                    return buffer;
                }
                return new float[2048];
            }
            else if (size <= 4096)
            {
                if (_buffer4096.TryDequeue(out var buffer))
                {
                    Interlocked.Decrement(ref _count4096);
                    return buffer;
                }
                return new float[4096];
            }
            else if (size <= 8192)
            {
                if (_buffer8192.TryDequeue(out var buffer))
                {
                    Interlocked.Decrement(ref _count8192);
                    return buffer;
                }
                return new float[8192];
            }
            else
            {
                // For very large buffers, check pool or allocate exact size
                if (_largeBuffers.TryDequeue(out var buffer) && buffer.Length >= size)
                {
                    Interlocked.Decrement(ref _countLarge);
                    return buffer;
                }
                return new float[size];
            }
        }

        /// <summary>
        /// Returns a buffer to the pool for reuse if there's space available.
        /// Buffers are only pooled if they match the expected sizes and the pool isn't full.
        /// </summary>
        /// <param name="buffer">The buffer to return to the pool. Can be null.</param>
        public static void Return(float[]? buffer)
        {
            if (buffer == null) return;

            int size = buffer.Length;

            // Clear the buffer before returning to pool (security & correctness)
            FastClear(buffer, size);

            // Return to appropriate pool based on size
            if (size == 512 && _count512 < MAX_POOL_SIZE)
            {
                _buffer512.Enqueue(buffer);
                Interlocked.Increment(ref _count512);
            }
            else if (size == 1024 && _count1024 < MAX_POOL_SIZE)
            {
                _buffer1024.Enqueue(buffer);
                Interlocked.Increment(ref _count1024);
            }
            else if (size == 2048 && _count2048 < MAX_POOL_SIZE)
            {
                _buffer2048.Enqueue(buffer);
                Interlocked.Increment(ref _count2048);
            }
            else if (size == 4096 && _count4096 < MAX_POOL_SIZE)
            {
                _buffer4096.Enqueue(buffer);
                Interlocked.Increment(ref _count4096);
            }
            else if (size == 8192 && _count8192 < MAX_POOL_SIZE)
            {
                _buffer8192.Enqueue(buffer);
                Interlocked.Increment(ref _count8192);
            }
            else if (size > 8192 && _countLarge < MAX_POOL_SIZE)
            {
                _largeBuffers.Enqueue(buffer);
                Interlocked.Increment(ref _countLarge);
            }
            // Buffers that don't match standard sizes or when pool is full are left for GC
        }

        /// <summary>
        /// Clears all buffers from all pools and resets counters.
        /// This method can be used to free up memory when the pool is no longer needed.
        /// </summary>
        public static void Clear()
        {
            while (_buffer512.TryDequeue(out _)) { }
            while (_buffer1024.TryDequeue(out _)) { }
            while (_buffer2048.TryDequeue(out _)) { }
            while (_buffer4096.TryDequeue(out _)) { }
            while (_buffer8192.TryDequeue(out _)) { }
            while (_largeBuffers.TryDequeue(out _)) { }

            _count512 = _count1024 = _count2048 = _count4096 = _count8192 = _countLarge = 0;
        }

        /// <summary>
        /// Gets diagnostic information about the current state of all buffer pools.
        /// </summary>
        /// <returns>A string containing pool statistics.</returns>
        public static string GetPoolStats()
        {
            return $"Pool Stats: 512={_count512}, 1024={_count1024}, 2048={_count2048}, " +
                   $"4096={_count4096}, 8192={_count8192}, Large={_countLarge}";
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
        private static void FastClear(float[]? buffer, int length)
        {
            if (buffer == null) return;

            int clearLength = Math.Min(buffer.Length, length);

            if (clearLength <= 1024)
                buffer.AsSpan(0, clearLength).Clear();
            else
                Array.Clear(buffer, 0, clearLength);
        }
    }
}
