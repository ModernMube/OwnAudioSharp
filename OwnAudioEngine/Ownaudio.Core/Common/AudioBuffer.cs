using System;

namespace Ownaudio.Core.Common;

/// <summary>
/// Simple byte buffer wrapper for audio decoding operations.
/// Provides a span-based interface to minimize allocations.
/// </summary>
public sealed class AudioBuffer
{
    private byte[] _buffer;

    /// <summary>
    /// Gets a span view of the entire buffer.
    /// </summary>
    public Span<byte> Data => _buffer.AsSpan();

    /// <summary>
    /// Gets the capacity of the buffer.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioBuffer"/> class with the specified capacity.
    /// </summary>
    /// <param name="capacity">The capacity of the buffer in bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when capacity is less than or equal to zero.</exception>
    public AudioBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");

        _buffer = new byte[capacity];
    }

    /// <summary>
    /// Gets the underlying buffer array.
    /// </summary>
    /// <returns>The internal byte array.</returns>
    public byte[] GetBuffer() => _buffer;

    /// <summary>
    /// Gets a span of the buffer cast to the specified type.
    /// </summary>
    /// <typeparam name="T">The target type for the span.</typeparam>
    /// <returns>A span view of the buffer as the specified type.</returns>
    public Span<T> AsSpan<T>() where T : struct
    {
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(_buffer.AsSpan());
    }

    /// <summary>
    /// Clears the buffer (sets all bytes to zero).
    /// </summary>
    public void Clear()
    {
        _buffer.AsSpan().Clear();
    }
}
