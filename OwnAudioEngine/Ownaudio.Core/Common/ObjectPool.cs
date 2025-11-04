using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Ownaudio.Core.Common
{
    /// <summary>
    /// Thread-safe object pool for reducing GC pressure.
    /// Used for pooling audio buffers and temporary objects.
    /// </summary>
    /// <typeparam name="T">Type of object to pool.</typeparam>
    public sealed class ObjectPool<T> where T : class
    {
        private readonly ConcurrentBag<T> _objects;
        private readonly Func<T> _objectGenerator;
        private readonly Action<T> _resetAction;
        private readonly int _maxSize;
        private int _currentSize;

        /// <summary>
        /// Creates a new object pool.
        /// </summary>
        /// <param name="objectGenerator">Factory function to create new instances.</param>
        /// <param name="resetAction">Optional action to reset objects before returning to pool.</param>
        /// <param name="initialSize">Initial number of objects to pre-allocate.</param>
        /// <param name="maxSize">Maximum pool size (0 = unlimited).</param>
        public ObjectPool(
            Func<T> objectGenerator,
            Action<T> resetAction = null,
            int initialSize = 0,
            int maxSize = 0)
        {
            if (objectGenerator == null)
                throw new ArgumentNullException(nameof(objectGenerator));

            _objectGenerator = objectGenerator;
            _resetAction = resetAction;
            _maxSize = maxSize;
            _objects = new ConcurrentBag<T>();
            _currentSize = 0;

            // Pre-allocate initial objects
            for (int i = 0; i < initialSize; i++)
            {
                _objects.Add(_objectGenerator());
                _currentSize++;
            }
        }

        /// <summary>
        /// Gets an object from the pool, or creates a new one if pool is empty.
        /// This method is thread-safe.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get()
        {
            if (_objects.TryTake(out T item))
            {
                return item;
            }

            // Pool is empty, create new instance
            return _objectGenerator();
        }

        /// <summary>
        /// Returns an object to the pool.
        /// This method is thread-safe.
        /// </summary>
        /// <param name="item">Object to return to pool.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(T item)
        {
            if (item == null)
                return;

            // Check max size limit
            if (_maxSize > 0 && _currentSize >= _maxSize)
                return; // Discard object

            // Reset object if reset action is provided
            _resetAction?.Invoke(item);

            _objects.Add(item);
            _currentSize++;
        }

        /// <summary>
        /// Clears the pool and releases all pooled objects.
        /// </summary>
        public void Clear()
        {
            while (_objects.TryTake(out _))
            {
                _currentSize--;
            }
        }

        /// <summary>
        /// Gets the approximate current size of the pool.
        /// This is an estimate due to concurrent access.
        /// </summary>
        public int Count => _objects.Count;
    }

    /// <summary>
    /// Specialized pool for float arrays used in audio processing.
    /// Pre-configured for audio buffer pooling with automatic sizing.
    /// </summary>
    public sealed class AudioBufferPool
    {
        private readonly ObjectPool<float[]> _pool;
        private readonly int _bufferSize;

        /// <summary>
        /// Creates a new audio buffer pool.
        /// </summary>
        /// <param name="bufferSize">Size of each buffer in samples.</param>
        /// <param name="initialPoolSize">Number of buffers to pre-allocate.</param>
        /// <param name="maxPoolSize">Maximum number of buffers to keep in pool.</param>
        public AudioBufferPool(int bufferSize, int initialPoolSize = 4, int maxPoolSize = 16)
        {
            _bufferSize = bufferSize;
            _pool = new ObjectPool<float[]>(
                objectGenerator: () => new float[bufferSize],
                resetAction: buffer => Array.Clear(buffer, 0, buffer.Length),
                initialSize: initialPoolSize,
                maxSize: maxPoolSize
            );
        }

        /// <summary>
        /// Gets a buffer from the pool.
        /// Buffer is cleared (all zeros) before returning.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float[] Get() => _pool.Get();

        /// <summary>
        /// Returns a buffer to the pool.
        /// Buffer will be cleared automatically.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(float[] buffer)
        {
            if (buffer != null && buffer.Length == _bufferSize)
            {
                _pool.Return(buffer);
            }
        }

        /// <summary>
        /// Gets the buffer size for this pool.
        /// </summary>
        public int BufferSize => _bufferSize;

        /// <summary>
        /// Clears the pool.
        /// </summary>
        public void Clear() => _pool.Clear();
    }
}