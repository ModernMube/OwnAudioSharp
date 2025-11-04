using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Ownaudio.Decoders.Flac;

/// <summary>
/// Zero-allocation bit reader for FLAC decoding.
/// Reads bits from a byte buffer with proper handling of bit-aligned data.
/// </summary>
internal ref struct FlacBitReader
{
    private ReadOnlySpan<byte> _buffer;
    private int _bytePosition;
    private int _bitPosition;
    private ulong _cache;
    private int _cacheBits;

    public FlacBitReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _bytePosition = 0;
        _bitPosition = 0;
        _cache = 0;
        _cacheBits = 0;
    }

    /// <summary>
    /// Gets current byte position in buffer.
    /// </summary>
    public int BytePosition => _bytePosition;

    /// <summary>
    /// Gets current bit position within current byte (0-7).
    /// </summary>
    public int BitPosition => _bitPosition;

    /// <summary>
    /// Gets total bit position.
    /// </summary>
    public long TotalBitsRead => (_bytePosition * 8L) + _bitPosition;

    /// <summary>
    /// Returns true if there are more bytes to read.
    /// </summary>
    public bool HasMoreData => _bytePosition < _buffer.Length;

    /// <summary>
    /// Reads a single bit (0 or 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit()
    {
        if (_cacheBits == 0)
            FillCache();

        _cacheBits--;
        return (int)((_cache >> _cacheBits) & 1);
    }

    /// <summary>
    /// Reads up to 32 bits as unsigned integer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if (count == 0)
            return 0;

        if (count > 32)
            throw new ArgumentOutOfRangeException(nameof(count), "Cannot read more than 32 bits at once.");

        // Ensure we have enough bits in cache
        while (_cacheBits < count && _bytePosition < _buffer.Length)
            FillCache();

        if (_cacheBits < count)
            throw new InvalidOperationException("Not enough bits available in buffer.");

        _cacheBits -= count;
        uint result = (uint)((_cache >> _cacheBits) & ((1UL << count) - 1));

        return result;
    }

    /// <summary>
    /// Reads up to 64 bits as unsigned long.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadBits64(int count)
    {
        if (count == 0)
            return 0;

        if (count > 64)
            throw new ArgumentOutOfRangeException(nameof(count), "Cannot read more than 64 bits at once.");

        if (count <= 32)
            return ReadBits(count);

        // Read in two parts
        ulong high = ReadBits(count - 32);
        ulong low = ReadBits(32);
        return (high << 32) | low;
    }

    /// <summary>
    /// Reads signed integer with specified bit count (two's complement).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSignedBits(int count)
    {
        if (count == 0)
            return 0;

        uint unsigned = ReadBits(count);

        // Sign extend
        if ((unsigned & (1u << (count - 1))) != 0)
        {
            // Negative: sign extend
            return (int)(unsigned | (~0u << count));
        }

        return (int)unsigned;
    }

    /// <summary>
    /// Reads unary-encoded value (count of 0s until first 1).
    /// Used in Rice coding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadUnary()
    {
        int count = 0;
        while (ReadBit() == 0)
        {
            count++;
            if (count > 10000) // Safety check - reasonable max for FLAC
                throw new InvalidOperationException($"Unary value too large: {count} (possible bit stream corruption)");
        }
        return count;
    }

    /// <summary>
    /// Reads Rice-encoded signed integer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadRice(int parameter)
    {
        // Quotient (unary)
        int quotient = ReadUnary();

        // Remainder (binary) - only read if parameter > 0
        int remainder = 0;
        if (parameter > 0)
            remainder = (int)ReadBits(parameter);

        // Combine: value = (quotient << parameter) | remainder
        int unsignedValue = (quotient << parameter) | remainder;

        // Decode zig-zag encoding: 0 = 0, 1 = -1, 2 = 1, 3 = -2, 4 = 2...
        return (unsignedValue & 1) != 0 ? -((unsignedValue + 1) >> 1) : (unsignedValue >> 1);
    }

    /// <summary>
    /// Aligns to next byte boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AlignToByte()
    {
        // If we have bits remaining in cache, we need to align
        if (_cacheBits > 0)
        {
            // Calculate how many bits to skip to reach byte boundary
            int bitsToSkip = _cacheBits % 8;
            if (bitsToSkip > 0)
            {
                _cacheBits -= bitsToSkip;
            }
        }

        // Now calculate the correct byte position
        // _bytePosition is ahead due to cache, so we need to adjust
        int cachedBytes = _cacheBits / 8;
        _bytePosition -= cachedBytes;

        // Clear cache
        _cache = 0;
        _cacheBits = 0;
        _bitPosition = 0;
    }

    /// <summary>
    /// Reads a byte (must be byte-aligned).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        // Should be called after AlignToByte(), but check just in case
        if (_cacheBits > 0)
        {
            // Read from cache first
            if (_cacheBits >= 8)
            {
                _cacheBits -= 8;
                return (byte)((_cache >> _cacheBits) & 0xFF);
            }
            else
            {
                // Not enough bits in cache, need to align properly
                throw new InvalidOperationException("ReadByte called when not byte-aligned");
            }
        }

        if (_bytePosition >= _buffer.Length)
            throw new InvalidOperationException("End of buffer reached.");

        return _buffer[_bytePosition++];
    }

    /// <summary>
    /// Reads multiple bytes into destination span.
    /// </summary>
    public void ReadBytes(Span<byte> destination)
    {
        AlignToByte();

        if (_bytePosition + destination.Length > _buffer.Length)
            throw new InvalidOperationException("Not enough data in buffer.");

        _buffer.Slice(_bytePosition, destination.Length).CopyTo(destination);
        _bytePosition += destination.Length;
    }

    /// <summary>
    /// Reads UTF-8 encoded variable-length integer.
    /// Used for sample/frame numbers in FLAC.
    /// </summary>
    public long ReadUTF8()
    {
        AlignToByte();

        byte first = ReadByte();

        if ((first & 0x80) == 0)
        {
            // Single byte (0xxxxxxx)
            return first;
        }

        // Count leading ones
        int bytes = 0;
        byte mask = 0x80;
        while ((first & mask) != 0)
        {
            bytes++;
            mask >>= 1;
        }

        if (bytes < 2 || bytes > 7)
            throw new InvalidOperationException($"Invalid UTF-8 encoding: {bytes} bytes.");

        // Extract value from first byte
        long value = first & (0xFF >> (bytes + 1));

        // Read continuation bytes
        for (int i = 1; i < bytes; i++)
        {
            byte b = ReadByte();
            if ((b & 0xC0) != 0x80)
                throw new InvalidOperationException("Invalid UTF-8 continuation byte.");

            value = (value << 6) | (b & 0x3F);
        }

        return value;
    }

    /// <summary>
    /// Skips specified number of bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int count)
    {
        while (count > 0 && _cacheBits > 0)
        {
            int skip = Math.Min(count, _cacheBits);
            _cacheBits -= skip;
            count -= skip;
        }

        while (count >= 8)
        {
            _bytePosition++;
            count -= 8;
        }

        if (count > 0)
            ReadBits(count);
    }

    /// <summary>
    /// Fills the bit cache from buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillCache()
    {
        // Fill cache with up to 8 bytes (64 bits)
        while (_cacheBits <= 56 && _bytePosition < _buffer.Length)
        {
            _cache = (_cache << 8) | _buffer[_bytePosition++];
            _cacheBits += 8;
        }
    }

    /// <summary>
    /// Peeks at next N bits without consuming them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PeekBits(int count)
    {
        if (count > 32)
            throw new ArgumentOutOfRangeException(nameof(count));

        // Ensure cache has enough bits
        while (_cacheBits < count && _bytePosition < _buffer.Length)
            FillCache();

        if (_cacheBits < count)
            return 0;

        return (uint)((_cache >> (_cacheBits - count)) & ((1UL << count) - 1));
    }
}
