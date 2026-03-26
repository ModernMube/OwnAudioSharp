using System.Runtime.CompilerServices;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.BufferManagement;

/// <summary>
/// Lock-free single-producer single-consumer (SPSC) circular buffer for audio data.
/// This implementation is designed for zero-allocation in the hot path.
///
/// Thread Safety:
/// - Write() must only be called from ONE producer thread
/// - Read(), Peek(), Skip() must only be called from ONE consumer thread
/// - Clear() is NOT thread-safe and must be called when no other operations are in progress
/// - Available, IsEmpty, IsFull are thread-safe (volatile reads)
///
/// Performance:
/// - Write/Read: O(1) time complexity, zero allocations
/// - Capacity is rounded up to power-of-2 for efficient modulo operations
/// - AggressiveInlining on hot path methods
/// </summary>
public sealed class CircularBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;
    private volatile int _writePos;
    private volatile int _readPos;
    private int _available;

    // Thread-safe clear flag: set by any thread, consumed atomically inside Write/Read
    private volatile bool _clearRequested;

    private readonly object _lock = new object();

    /// <summary>
    /// Gets the capacity of the buffer in samples.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the number of samples available to read.
    /// Returns 0 immediately when a Clear() is pending, consistent with the deferred reset.
    /// </summary>
    public int Available => _clearRequested ? 0 : Volatile.Read(ref _available);

    /// <summary>
    /// Gets the number of samples that can be written.
    /// </summary>
    public int WritableCount => _capacity - Available;

    /// <summary>
    /// Gets whether the buffer is empty.
    /// </summary>
    public bool IsEmpty => Available == 0;

    /// <summary>
    /// Gets whether the buffer is full.
    /// </summary>
    public bool IsFull => Available == _capacity;

    /// <summary>
    /// Initializes a new instance of the CircularBuffer class.
    /// </summary>
    /// <param name="capacityInSamples">The capacity in samples (NOT frames).</param>
    public CircularBuffer(int capacityInSamples)
    {
        if (capacityInSamples <= 0)
            throw new AudioException("CircularBuffer ERROR: ", new ArgumentException("Capacity must be greater than zero.", nameof(capacityInSamples)));

        // Round up to power of 2 for efficient modulo operations
        _capacity = RoundUpToPowerOf2(capacityInSamples);
        _buffer = new float[_capacity];
        _writePos = 0;
        _readPos = 0;
        _available = 0;
    }

    /// <summary>
    /// Writes samples to the buffer.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <returns>The number of samples actually written.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Write(ReadOnlySpan<float> data)
    {
        // Consume a pending clear request atomically at the start of a write cycle.
        // This avoids the race where Clear() zeroes _available while a concurrent Write
        // is still running (which would make the subsequent Interlocked.Add go negative).
        if (_clearRequested)
        {
            lock (_lock)
            {
                if (_clearRequested)
                {
                    _writePos = 0;
                    _readPos = 0;
                    Volatile.Write(ref _available, 0);
                    _clearRequested = false;
                }
            }
        }

        int available = Volatile.Read(ref _available);
        int writable = _capacity - available;
        int toWrite = Math.Min(data.Length, writable);

        if (toWrite == 0)
            return 0;

        int writePos = _writePos; // Volatile read (implicit)
        int firstChunk = Math.Min(toWrite, _capacity - writePos);

        // Write first chunk
        data.Slice(0, firstChunk).CopyTo(_buffer.AsSpan(writePos, firstChunk));

        // Write second chunk if wrapping around
        if (toWrite > firstChunk)
        {
            int secondChunk = toWrite - firstChunk;
            data.Slice(firstChunk, secondChunk).CopyTo(_buffer.AsSpan(0, secondChunk));
        }

        // Update write position with volatile write (ensures visibility to consumer)
        _writePos = (writePos + toWrite) & (_capacity - 1); // Efficient modulo with power of 2

        // Memory barrier ensures buffer writes complete before available update
        // This guarantees the consumer won't read uninitialized data
        Thread.MemoryBarrier();

        // Update available count atomically (Interlocked.Add has full fence)
        Interlocked.Add(ref _available, toWrite);

        return toWrite;
    }

    /// <summary>
    /// Reads samples from the buffer.
    /// </summary>
    /// <param name="destination">The destination buffer.</param>
    /// <returns>The number of samples actually read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(Span<float> destination)
    {
        // Consume a pending clear request atomically at the start of a read cycle.
        // This avoids the race where Clear() zeroes _available while a concurrent Read
        // is still running (which would make the subsequent Interlocked.Add go negative).
        if (_clearRequested)
        {
            lock (_lock)
            {
                if (_clearRequested)
                {
                    _writePos = 0;
                    _readPos = 0;
                    Volatile.Write(ref _available, 0);
                    _clearRequested = false;
                }
            }
            // Return 0 – caller will fill with silence for this cycle
            return 0;
        }

        int available = Volatile.Read(ref _available);
        int toRead = Math.Min(destination.Length, available);

        if (toRead == 0)
            return 0;

        int readPos = _readPos; // Volatile read (implicit)
        int firstChunk = Math.Min(toRead, _capacity - readPos);

        // Read first chunk
        _buffer.AsSpan(readPos, firstChunk).CopyTo(destination.Slice(0, firstChunk));

        // Read second chunk if wrapping around
        if (toRead > firstChunk)
        {
            int secondChunk = toRead - firstChunk;
            _buffer.AsSpan(0, secondChunk).CopyTo(destination.Slice(firstChunk, secondChunk));
        }

        // Update read position with volatile write (ensures visibility to producer)
        _readPos = (readPos + toRead) & (_capacity - 1); // Efficient modulo with power of 2

        // Memory barrier ensures reads complete before available update
        Thread.MemoryBarrier();

        // Update available count atomically (Interlocked.Add has full fence)
        Interlocked.Add(ref _available, -toRead);

        return toRead;
    }

    /// <summary>
    /// Peeks at samples without consuming them.
    /// </summary>
    /// <param name="destination">The destination buffer.</param>
    /// <returns>The number of samples actually peeked.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Peek(Span<float> destination)
    {
        int available = Volatile.Read(ref _available);
        int toPeek = Math.Min(destination.Length, available);

        if (toPeek == 0)
            return 0;

        int readPos = _readPos;
        int firstChunk = Math.Min(toPeek, _capacity - readPos);

        // Peek first chunk
        _buffer.AsSpan(readPos, firstChunk).CopyTo(destination.Slice(0, firstChunk));

        // Peek second chunk if wrapping around
        if (toPeek > firstChunk)
        {
            int secondChunk = toPeek - firstChunk;
            _buffer.AsSpan(0, secondChunk).CopyTo(destination.Slice(firstChunk, secondChunk));
        }

        return toPeek;
    }

    /// <summary>
    /// Skips the specified number of samples.
    /// </summary>
    /// <param name="sampleCount">The number of samples to skip.</param>
    /// <returns>The number of samples actually skipped.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Skip(int sampleCount)
    {
        int available = Volatile.Read(ref _available);
        int toSkip = Math.Min(sampleCount, available);

        if (toSkip == 0)
            return 0;

        _readPos = (_readPos + toSkip) & (_capacity - 1);
        Interlocked.Add(ref _available, -toSkip);

        return toSkip;
    }

    /// <summary>
    /// Requests a thread-safe clear of the buffer.
    /// The actual reset is applied atomically inside the next Write() or Read() call
    /// by the producer/consumer thread, avoiding the race condition where resetting
    /// _available to 0 mid-operation causes it to go negative.
    /// </summary>
    public void Clear()
    {
        _clearRequested = true;
    }

    /// <summary>
    /// Clears the buffer and zeros out the array to prevent residual audio.
    /// Uses the same deferred thread-safe mechanism as Clear().
    /// The zero-fill of the backing array happens immediately (safe because float[] writes
    /// to positions outside [readPos, writePos) are never consumed).
    /// </summary>
    public void ClearWithZero()
    {
        Array.Clear(_buffer, 0, _capacity);
        _clearRequested = true;
    }

    /// <summary>
    /// Rounds up to the nearest power of 2.
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
