using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ownaudio.Core.Common
{
    /// <summary>
    /// SPSC ring buffer for the RT path. One reader, one writer, no locks,
    /// no allocation once it is built. Capacity is rounded to a power of 2.
    /// </summary>
    /// <typeparam name="T">Element type, float for samples.</typeparam>
    public sealed class LockFreeRingBuffer<T> where T : struct
    {
        private readonly T[] _buffer;
        private readonly int _capacity;
        private readonly int _capacityMask;

        private int _writeIndex;
        private int _readIndex;

        /// <summary>
        /// Slots in the buffer after the power-of-2 rounding.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Elements sitting there waiting to be read.
        /// </summary>
        public int Available
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int _write = Volatile.Read(ref _writeIndex);
                int _read = Volatile.Read(ref _readIndex);
                return (_write - _read + _capacity) & _capacityMask;
            }
        }

        /// <summary>
        /// Same as Available, kept for the monitoring code.
        /// </summary>
        public int AvailableRead
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Available;
        }

        /// <summary>
        /// Room left for writing. One slot is burned to tell full from empty.
        /// </summary>
        public int WritableCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _capacity - Available - 1;
        }

        /// <summary></summary>
        /// <param name="capacity"></param>
        public LockFreeRingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacity));

            _capacity = _roundUpToPowerOf2(capacity);
            _capacityMask = _capacity - 1;
            _buffer = new T[_capacity];
        }

        /// <summary>
        /// Pushes what fits, wrapping around the end.
        /// </summary>
        /// <returns>How many elements actually landed in there.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Write(ReadOnlySpan<T> data)
        {
            int _toWrite = Math.Min(data.Length, WritableCount);
            if (_toWrite == 0) return 0;

            int _writeIdx = Volatile.Read(ref _writeIndex);
            int _firstChunk = Math.Min(_toWrite, _capacity - _writeIdx);

            data.Slice(0, _firstChunk).CopyTo(_buffer.AsSpan(_writeIdx, _firstChunk));

            if (_toWrite > _firstChunk)
                data.Slice(_firstChunk, _toWrite - _firstChunk).CopyTo(_buffer.AsSpan(0, _toWrite - _firstChunk));

            Volatile.Write(ref _writeIndex, (_writeIdx + _toWrite) & _capacityMask);
            return _toWrite;
        }

        /// <summary>
        /// Pulls what it can into destination, wrapping the same way.
        /// </summary>
        /// <returns>How many elements we managed to read.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read(Span<T> destination)
        {
            int _toRead = Math.Min(destination.Length, Available);
            if(_toRead == 0) return 0;

            int _readIdx = Volatile.Read(ref _readIndex);
            int _firstChunk = Math.Min(_toRead, _capacity - _readIdx);

            _buffer.AsSpan(_readIdx, _firstChunk).CopyTo(destination.Slice(0, _firstChunk));

            if (_toRead > _firstChunk)
                _buffer.AsSpan(0, _toRead - _firstChunk).CopyTo(destination.Slice(_firstChunk, _toRead - _firstChunk));

            Volatile.Write(ref _readIndex, (_readIdx + _toRead) & _capacityMask);
            return _toRead;
        }

        /// <summary>
        /// Rewinds both indices. Not safe while anyone is reading or writing.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _writeIndex = 0;
            _readIndex = 0;
        }

        /// <summary>
        /// Next power of 2 at or above value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int _roundUpToPowerOf2(int value)
        {
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }
    }
}
