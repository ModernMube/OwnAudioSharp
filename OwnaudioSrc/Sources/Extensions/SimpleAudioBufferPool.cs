using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ownaudio.Sources.Extensions
{
    // **SIMPLIFIED BUFFER POOL** - Csak nagy buffer-ekhez, minimál overhead
    public static class SimpleAudioBufferPool
    {
        private static readonly ConcurrentQueue<float[]> _smallBuffers = new(); // 512-1024
        private static readonly ConcurrentQueue<float[]> _mediumBuffers = new(); // 1024-2048  
        private static readonly ConcurrentQueue<float[]> _largeBuffers = new(); // 2048+

        private static int _smallCount = 0;
        private static int _mediumCount = 0;
        private static int _largeCount = 0;

        private const int MAX_POOL_SIZE = 10; // Kis pool méret = kevesebb memory usage

        public static float[] Rent(int size)
        {
            // Kis buffer-eknél ne használjunk pool-t - egyszerű allokáció gyorsabb
            if (size <= 256)
                return new float[size];

            if (size <= 1024)
            {
                if (_smallBuffers.TryDequeue(out var buffer))
                {
                    Interlocked.Decrement(ref _smallCount);
                    Array.Clear(buffer, 0, size); // Csak a szükséges részt töröljük
                    return buffer;
                }
                return new float[1024]; // Fix méret jobb cache-hez
            }
            else if (size <= 2048)
            {
                if (_mediumBuffers.TryDequeue(out var buffer))
                {
                    Interlocked.Decrement(ref _mediumCount);
                    Array.Clear(buffer, 0, size);
                    return buffer;
                }
                return new float[2048];
            }
            else
            {
                if (_largeBuffers.TryDequeue(out var buffer) && buffer.Length >= size)
                {
                    Interlocked.Decrement(ref _largeCount);
                    Array.Clear(buffer, 0, size);
                    return buffer;
                }
                return new float[size];
            }
        }

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
            // Kis buffer-eket és tele pool esetén hagyjuk a GC-re
        }

        public static void Clear()
        {
            while (_smallBuffers.TryDequeue(out _)) { }
            while (_mediumBuffers.TryDequeue(out _)) { }
            while (_largeBuffers.TryDequeue(out _)) { }
            _smallCount = _mediumCount = _largeCount = 0;
        }
    }
}
