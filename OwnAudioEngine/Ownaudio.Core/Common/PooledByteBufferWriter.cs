using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Ownaudio.Core.Common;

/// <summary>
/// Zero-allocation byte buffer writer using ArrayPool for temporary storage.
/// Replaces MemoryStream in DecodeAllFrames() to eliminate GC pressure.
/// </summary>
/// <remarks>
/// This class provides a growable buffer backed by ArrayPool&lt;byte&gt;.
/// It automatically expands capacity as needed and returns buffers to the pool on disposal.
///
/// Performance characteristics:
/// - Zero allocations for writes (uses pooled arrays)
/// - Automatic capacity growth (2x expansion)
/// - Thread-safe disposal
/// - Minimal GC pressure
///
/// Usage:
/// using var writer = new PooledByteBufferWriter(initialCapacity: 4096);
/// writer.Write(data);
/// byte[] result = writer.ToArray(); // Single allocation for final result
/// </remarks>
public sealed class PooledByteBufferWriter : IDisposable
{
    private byte[]? _buffer;
    private int _position;
    private int _capacity;
    private bool _disposed;

    /// <summary>
    /// Gets the current position in the buffer.
    /// </summary>
    public int Position => _position;

    /// <summary>
    /// Gets the current capacity of the buffer.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the underlying buffer as a span (up to current position).
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);

    /// <summary>
    /// Creates a new pooled buffer writer with specified initial capacity.
    /// </summary>
    /// <param name="initialCapacity">Initial capacity in bytes (default: 4096).</param>
    public PooledByteBufferWriter(int initialCapacity = 4096)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));

        _capacity = initialCapacity;
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _position = 0;
        _disposed = false;
    }

    /// <summary>
    /// Writes a byte array to the buffer, expanding capacity if needed.
    /// </summary>
    /// <param name="data">Data to write.</param>
    /// <param name="offset">Offset in source data.</param>
    /// <param name="count">Number of bytes to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(byte[] data, int offset, int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PooledByteBufferWriter));

        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (offset < 0 || count < 0 || offset + count > data.Length)
            throw new ArgumentOutOfRangeException();

        EnsureCapacity(_position + count);

        Array.Copy(data, offset, _buffer, _position, count);
        _position += count;
    }

    /// <summary>
    /// Writes a span to the buffer, expanding capacity if needed.
    /// </summary>
    /// <param name="data">Data to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PooledByteBufferWriter));

        EnsureCapacity(_position + data.Length);

        data.CopyTo(_buffer.AsSpan(_position));
        _position += data.Length;
    }

    /// <summary>
    /// Ensures the buffer has at least the specified capacity.
    /// Expands by 2x if needed, using ArrayPool.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= _capacity)
            return;

        // Calculate new capacity (2x growth)
        int newCapacity = Math.Max(requiredCapacity, _capacity * 2);

        // Rent new buffer from pool
        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);

        // Copy existing data
        if (_position > 0)
        {
            Array.Copy(_buffer, 0, newBuffer, 0, _position);
        }

        // Return old buffer to pool
        ArrayPool<byte>.Shared.Return(_buffer);

        // Update state
        _buffer = newBuffer;
        _capacity = newBuffer.Length;
    }

    /// <summary>
    /// Converts the written data to a byte array.
    /// This allocates a new array of exact size.
    /// </summary>
    /// <returns>A new byte array containing the written data.</returns>
    public byte[] ToArray()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PooledByteBufferWriter));

        byte[] result = new byte[_position];
        Array.Copy(_buffer, 0, result, 0, _position);
        return result;
    }

    /// <summary>
    /// Resets the position to zero without releasing the buffer.
    /// Useful for reusing the writer.
    /// </summary>
    public void Reset()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PooledByteBufferWriter));

        _position = 0;
    }

    /// <summary>
    /// Releases the pooled buffer back to ArrayPool.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_buffer != null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}
