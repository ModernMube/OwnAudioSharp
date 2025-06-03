using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace Ownaudio.Sources.Extensions
{
    /// <summary>
    /// Provides thread-safe pooling of float arrays for audio buffer management.
    /// Optimizes memory allocation by reusing buffers of common sizes.
    /// </summary>
    public static class AudioBufferPool
    {
        /// <summary>
        /// Dictionary storing pools of buffers organized by buffer size.
        /// </summary>
        private static readonly ConcurrentDictionary<int, ConcurrentQueue<float[]>> _pools = new();

        /// <summary>
        /// Dictionary tracking the current count of buffers in each pool.
        /// </summary>
        private static readonly ConcurrentDictionary<int, int> _poolSizes = new();

        /// <summary>
        /// Maximum number of buffers to keep in each size bucket to prevent unbounded memory growth.
        /// </summary>
        private const int MAX_POOL_SIZE_PER_BUCKET = 20;

        /// <summary>
        /// Rents a float array from the pool with at least the specified minimum size.
        /// The actual size will be rounded up to the next power of 2 for better pooling efficiency.
        /// </summary>
        /// <param name="minimumSize">The minimum required size of the buffer.</param>
        /// <returns>A float array with size equal to or greater than the minimum size.</returns>
        public static float[] Rent(int minimumSize)
        {
            // Round up to next power of 2 for better pooling
            int size = GetNextPowerOf2(minimumSize);

            var pool = _pools.GetOrAdd(size, _ => new ConcurrentQueue<float[]>());

            if (pool.TryDequeue(out var buffer))
            {
                _poolSizes.AddOrUpdate(size, 0, (k, v) => Math.Max(0, v - 1));
                return buffer;
            }

            return new float[size];
        }

        /// <summary>
        /// Returns a buffer to the pool for reuse. The buffer will be cleared before being added to the pool.
        /// If the pool for this buffer size is full, the buffer will be discarded to prevent memory leaks.
        /// </summary>
        /// <param name="buffer">The buffer to return to the pool. Can be null (will be ignored).</param>
        public static void Return(float[] buffer)
        {
            if (buffer == null) return;

            int size = buffer.Length;
            var pool = _pools.GetOrAdd(size, _ => new ConcurrentQueue<float[]>());

            int currentSize = _poolSizes.GetOrAdd(size, 0);
            if (currentSize < MAX_POOL_SIZE_PER_BUCKET)
            {
                // Clear buffer before returning to pool
                Array.Clear(buffer, 0, buffer.Length);
                pool.Enqueue(buffer);
                _poolSizes.AddOrUpdate(size, 1, (k, v) => v + 1);
            }
        }

        /// <summary>
        /// Calculates the next power of 2 that is greater than or equal to the specified value.
        /// This helps optimize buffer pooling by standardizing buffer sizes.
        /// </summary>
        /// <param name="value">The input value.</param>
        /// <returns>The next power of 2 greater than or equal to the input value.</returns>
        private static int GetNextPowerOf2(int value)
        {
            if (value <= 0) return 1;

            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }

        /// <summary>
        /// Clears all pools and resets pool size counters. 
        /// This releases all pooled buffers and allows them to be garbage collected.
        /// </summary>
        public static void Clear()
        {
            _pools.Clear();
            _poolSizes.Clear();
        }
    }

    /// <summary>
    /// Provides SIMD-optimized functions for audio buffer operations.
    /// Falls back to scalar implementations when SIMD is not available.
    /// </summary>
    public static class SimdMixingHelper
    {
        /// <summary>
        /// Mixes (adds) source audio buffer into destination buffer using SIMD optimization when available.
        /// The result is clamped to the range [-1.0, 1.0] to prevent audio clipping.
        /// </summary>
        /// <param name="source">The source audio buffer to mix from.</param>
        /// <param name="destination">The destination audio buffer to mix into. This buffer is modified in-place.</param>
        public static void MixBuffersSimd(ReadOnlySpan<float> source, Span<float> destination)
        {
            if (Vector.IsHardwareAccelerated && source.Length >= Vector<float>.Count)
            {
                int vectorSize = Vector<float>.Count;
                int vectorLength = destination.Length - (destination.Length % vectorSize);

                // SIMD processing
                for (int i = 0; i < vectorLength; i += vectorSize)
                {
                    var sourceVec = new Vector<float>(source.Slice(i));
                    var destVec = new Vector<float>(destination.Slice(i));
                    var result = Vector.Add(sourceVec, destVec);

                    // Clamp to [-1.0, 1.0]
                    var minVec = new Vector<float>(-1.0f);
                    var maxVec = new Vector<float>(1.0f);
                    result = Vector.Max(result, minVec);
                    result = Vector.Min(result, maxVec);

                    result.CopyTo(destination.Slice(i));
                }

                // Handle remaining elements
                for (int i = vectorLength; i < destination.Length; i++)
                {
                    destination[i] += source[i];
                    destination[i] = Math.Clamp(destination[i], -1.0f, 1.0f);
                }
            }
            else
            {
                // Fallback to scalar implementation
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] += source[i];
                    destination[i] = Math.Clamp(destination[i], -1.0f, 1.0f);
                }
            }
        }

        /// <summary>
        /// Clears (zeros out) an audio buffer using SIMD optimization when available.
        /// This is more efficient than Array.Clear for large buffers on SIMD-capable hardware.
        /// </summary>
        /// <param name="buffer">The audio buffer to clear. This buffer is modified in-place.</param>
        public static void ClearBufferSimd(Span<float> buffer)
        {
            if (Vector.IsHardwareAccelerated && buffer.Length >= Vector<float>.Count)
            {
                int vectorSize = Vector<float>.Count;
                int vectorLength = buffer.Length - (buffer.Length % vectorSize);
                var zeroVec = Vector<float>.Zero;

                for (int i = 0; i < vectorLength; i += vectorSize)
                {
                    zeroVec.CopyTo(buffer.Slice(i));
                }

                // Handle remaining elements
                for (int i = vectorLength; i < buffer.Length; i++)
                {
                    buffer[i] = 0.0f;
                }
            }
            else
            {
                buffer.Clear();
            }
        }
    }
}
