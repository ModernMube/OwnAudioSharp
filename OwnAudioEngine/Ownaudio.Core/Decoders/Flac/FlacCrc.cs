using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Decoders.Flac;

/// <summary>
/// CRC-8 and CRC-16 calculation for FLAC.
/// Pre-computed lookup tables for fast calculation.
/// </summary>
internal static class FlacCrc
{
    // CRC-8 lookup table (polynomial: x^8 + x^2 + x^1 + x^0)
    private static readonly byte[] Crc8Table = new byte[256];

    // CRC-16 lookup table (polynomial: x^16 + x^15 + x^2 + x^0)
    private static readonly ushort[] Crc16Table = new ushort[256];

    static FlacCrc()
    {
        // Initialize CRC-8 table
        for (int i = 0; i < 256; i++)
        {
            byte crc = (byte)i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x80) != 0)
                    crc = (byte)((crc << 1) ^ 0x07);
                else
                    crc = (byte)(crc << 1);
            }
            Crc8Table[i] = crc;
        }

        // Initialize CRC-16 table (ANSI variant used by FLAC)
        for (int i = 0; i < 256; i++)
        {
            ushort crc = 0;
            ushort c = (ushort)(i << 8);

            for (int j = 0; j < 8; j++)
            {
                if (((crc ^ c) & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ 0x8005);
                else
                    crc = (ushort)(crc << 1);

                c = (ushort)(c << 1);
            }

            Crc16Table[i] = crc;
        }
    }

    /// <summary>
    /// Calculates CRC-8 checksum for the given data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte CalculateCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc = Crc8Table[crc ^ b];
        }
        return crc;
    }

    /// <summary>
    /// Updates CRC-8 with a single byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte UpdateCrc8(byte crc, byte data)
    {
        return Crc8Table[crc ^ data];
    }

    /// <summary>
    /// Calculates CRC-16 checksum for the given data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort CalculateCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ Crc16Table[((crc >> 8) ^ b) & 0xFF]);
        }
        return crc;
    }

    /// <summary>
    /// Updates CRC-16 with a single byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort UpdateCrc16(ushort crc, byte data)
    {
        return (ushort)((crc << 8) ^ Crc16Table[((crc >> 8) ^ data) & 0xFF]);
    }

    /// <summary>
    /// Updates CRC-16 with multiple bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort UpdateCrc16(ushort crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ Crc16Table[((crc >> 8) ^ b) & 0xFF]);
        }
        return crc;
    }
}
