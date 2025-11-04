using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ownaudio.Core.Common
{
    /// <summary>
    /// Lock-free single-producer single-consumer ring buffer for real-time audio.
    /// Zero allocation after construction. Thread-safe for one reader and one writer.
    /// </summary>
    /// <typeparam name="T">Element type (use float for audio samples).</typeparam>
    public sealed class LockFreeRingBuffer<T> where T : struct
    {
        private readonly T[] _buffer;
        private readonly int _capacity;
        private readonly int _capacityMask; // For power-of-2 optimization

        // Use explicit Volatile access instead of volatile keyword for better control
        private int _writeIndex;
        private int _readIndex;

        /// <summary>
        /// Gets the capacity of the ring buffer.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Gets the number of elements available to read.
        /// </summary>
        public int Available
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int write = Volatile.Read(ref _writeIndex);
                int read = Volatile.Read(ref _readIndex);
                return (write - read + _capacity) & _capacityMask;
            }
        }

        /// <summary>
        /// Gets the number of elements available to read (alias for monitoring).
        /// </summary>
        public int AvailableRead
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Available;
        }

        /// <summary>
        /// Gets the number of elements available to write.
        /// </summary>
        public int WritableCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _capacity - Available - 1; // -1 to avoid full/empty ambiguity
        }

        /// <summary>
        /// Creates a lock-free ring buffer with the specified capacity.
        /// Capacity must be a power of 2 for optimal performance.
        /// </summary>
        /// <param name="capacity">Buffer capacity (will be rounded up to next power of 2).</param>
        public LockFreeRingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacity));

            // Round up to next power of 2
            _capacity = RoundUpToPowerOf2(capacity);
            _capacityMask = _capacity - 1;
            _buffer = new T[_capacity];
            _writeIndex = 0;
            _readIndex = 0;
        }

        /// <summary>
        /// Writes elements to the ring buffer.
        /// This method is real-time safe (zero allocation).
        /// </summary>
        /// <param name="data">Data to write.</param>
        /// <returns>Number of elements actually written.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Write(ReadOnlySpan<T> data)
        {
            int available = WritableCount;
            int toWrite = Math.Min(data.Length, available);

            if (toWrite == 0)
                return 0;

            int writeIdx = Volatile.Read(ref _writeIndex);
            int firstChunk = Math.Min(toWrite, _capacity - writeIdx);

            // Copy first chunk
            data.Slice(0, firstChunk).CopyTo(_buffer.AsSpan(writeIdx, firstChunk));

            // Copy wraparound chunk if needed
            if (toWrite > firstChunk)
            {
                int remaining = toWrite - firstChunk;
                data.Slice(firstChunk, remaining).CopyTo(_buffer.AsSpan(0, remaining));
            }

            // Update write index with volatile write (ensures visibility to reader thread)
            Volatile.Write(ref _writeIndex, (writeIdx + toWrite) & _capacityMask);

            return toWrite;
        }

        /// <summary>
        /// Reads elements from the ring buffer.
        /// This method is real-time safe (zero allocation).
        /// </summary>
        /// <param name="destination">Destination buffer.</param>
        /// <returns>Number of elements actually read.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read(Span<T> destination)
        {
            int available = Available;
            int toRead = Math.Min(destination.Length, available);

            if (toRead == 0)
                return 0;

            int readIdx = Volatile.Read(ref _readIndex);
            int firstChunk = Math.Min(toRead, _capacity - readIdx);

            // Copy first chunk
            _buffer.AsSpan(readIdx, firstChunk).CopyTo(destination.Slice(0, firstChunk));

            // Copy wraparound chunk if needed
            if (toRead > firstChunk)
            {
                int remaining = toRead - firstChunk;
                _buffer.AsSpan(0, remaining).CopyTo(destination.Slice(firstChunk, remaining));
            }

            // Update read index with volatile write (ensures visibility to writer thread)
            Volatile.Write(ref _readIndex, (readIdx + toRead) & _capacityMask);

            return toRead;
        }

        /// <summary>
        /// Clears the ring buffer (resets read/write indices).
        /// Not thread-safe - call only when no concurrent access occurs.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _writeIndex = 0;
            _readIndex = 0;
        }

        /// <summary>
        /// Rounds up to the next power of 2.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RoundUpToPowerOf2(int value)
        {
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            value++;
            return value;
        }
    }
}