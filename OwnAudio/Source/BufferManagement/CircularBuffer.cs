using System.Runtime.CompilerServices;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.BufferManagement;

/// <summary>
/// Lock-free SPSC ring buffer for audio. One producer calls Write, one consumer
/// calls Read/Peek/Skip. Capacity gets rounded up to a power of two so the wrap
/// is a cheap mask instead of a modulo.
/// </summary>
public sealed class CircularBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;
    private readonly int _mask;
    private volatile int _writePos;
    private volatile int _readPos;
    private int _available;

    /// <summary>
    /// Set by Clear(), consumed by whichever side touches the buffer next.
    /// </summary>
    private volatile bool _clearRequested;

    private readonly object _lock = new object();

    /// <summary>
    /// Buffer size in samples, after the power-of-two rounding.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Samples waiting to be read. Reports 0 while a Clear is still pending.
    /// </summary>
    public int Available => _clearRequested ? 0 : Volatile.Read(ref _available);

    /// <summary>
    /// How much room is left for writing.
    /// </summary>
    public int WritableCount => _capacity - Available;

    public bool IsEmpty => Available == 0;

    public bool IsFull => Available == _capacity;

    /// <summary>
    /// Capacity is given in samples, not frames.
    /// </summary>
    /// <param name="capacityInSamples"></param>
    public CircularBuffer(int capacityInSamples)
    {
        if (capacityInSamples <= 0)
            throw new AudioException("CircularBuffer ERROR: ", new ArgumentException("Capacity must be greater than zero.", nameof(capacityInSamples)));

        _capacity = _roundUpPow2(capacityInSamples);
        _mask = _capacity - 1;
        _buffer = new float[_capacity];
    }

    /// <summary>
    /// Pushes samples in. If a Clear is pending we swallow the data and return 0,
    /// that way stale pre-seek stuff never sneaks back in.
    /// </summary>
    /// <returns>How many samples actually landed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Write(ReadOnlySpan<float> data)
    {
        if (_clearRequested) { _applyPendingClear(); return 0; }

        int writable = _capacity - Volatile.Read(ref _available);
        int toWrite = Math.Min(data.Length, writable);
        if (toWrite == 0) return 0;

        int writePos = _writePos;
        int firstChunk = Math.Min(toWrite, _capacity - writePos);

        data.Slice(0, firstChunk).CopyTo(_buffer.AsSpan(writePos, firstChunk));
        if (toWrite > firstChunk)
            data.Slice(firstChunk, toWrite - firstChunk).CopyTo(_buffer.AsSpan(0, toWrite - firstChunk));

        _writePos = (writePos + toWrite) & _mask;

        Thread.MemoryBarrier();
        Interlocked.Add(ref _available, toWrite);

        return toWrite;
    }

    /// <summary>
    /// Pulls samples out into destination.
    /// </summary>
    /// <returns>How many samples we managed to hand over.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(Span<float> destination)
    {
        if (_clearRequested) { _applyPendingClear(); return 0; }

        int toRead = Math.Min(destination.Length, Volatile.Read(ref _available));
        if (toRead == 0) return 0;

        int readPos = _readPos;
        int firstChunk = Math.Min(toRead, _capacity - readPos);

        _buffer.AsSpan(readPos, firstChunk).CopyTo(destination.Slice(0, firstChunk));
        if (toRead > firstChunk)
            _buffer.AsSpan(0, toRead - firstChunk).CopyTo(destination.Slice(firstChunk, toRead - firstChunk));

        _readPos = (readPos + toRead) & _mask;

        Thread.MemoryBarrier();
        Interlocked.Add(ref _available, -toRead);

        return toRead;
    }

    /// <summary>
    /// Same as Read but leaves the data in place.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Peek(Span<float> destination)
    {
        int toPeek = Math.Min(destination.Length, Volatile.Read(ref _available));
        if (toPeek == 0) return 0;

        int readPos = _readPos;
        int firstChunk = Math.Min(toPeek, _capacity - readPos);

        _buffer.AsSpan(readPos, firstChunk).CopyTo(destination.Slice(0, firstChunk));
        if (toPeek > firstChunk)
            _buffer.AsSpan(0, toPeek - firstChunk).CopyTo(destination.Slice(firstChunk, toPeek - firstChunk));

        return toPeek;
    }

    /// <summary>
    /// Drops up to sampleCount samples without copying them anywhere.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Skip(int sampleCount)
    {
        int toSkip = Math.Min(sampleCount, Volatile.Read(ref _available));
        if (toSkip == 0) return 0;

        _readPos = (_readPos + toSkip) & _mask;
        Thread.MemoryBarrier();
        Interlocked.Add(ref _available, -toSkip);

        return toSkip;
    }

    /// <summary>
    /// Asks for a reset. The real work happens on the next Write/Read from the
    /// owning thread — resetting _available here would race it negative.
    /// </summary>
    public void Clear()
    {
        _clearRequested = true;
    }

    /// <summary>
    /// Deferred clear plus an immediate wipe of the backing array, so no residual
    /// audio can be heard if something reads ahead.
    /// </summary>
    public void ClearWithZero()
    {
        Array.Clear(_buffer, 0, _capacity);
        _clearRequested = true;
    }

    private void _applyPendingClear()
    {
        lock (_lock)
        {
            if(!_clearRequested) return;

            _writePos = 0;
            _readPos = 0;
            Volatile.Write(ref _available, 0);
            _clearRequested = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _roundUpPow2(int value)
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
