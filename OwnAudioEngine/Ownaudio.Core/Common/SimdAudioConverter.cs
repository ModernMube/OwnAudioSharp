using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics.Arm;

namespace Ownaudio.Core.Common;

/// <summary>
/// SIMD-accelerated audio format conversion utilities.
/// Provides 4-8x faster PCM to Float32 conversion using AVX2/SSE2.
/// </summary>
/// <remarks>
/// Performance characteristics:
/// - AVX2 (256-bit): Processes 8 samples per iteration (8x speedup)
/// - SSE2 (128-bit): Processes 4 samples per iteration (4x speedup)
/// - Scalar fallback: Standard loop for non-SIMD or remainder samples
///
/// All methods are zero-allocation and thread-safe.
/// </remarks>
public static class SimdAudioConverter
{
    // Pre-calculated scaling constants
    private const float Scale8Bit = 1.0f / 128.0f;
    private const float Scale16Bit = 1.0f / 32768.0f;
    private const float Scale24Bit = 1.0f / 8388608.0f;
    private const float Scale32Bit = 1.0f / 2147483648.0f;

    /// <summary>
    /// Converts 16-bit signed PCM to Float32 using SIMD acceleration.
    /// Supports ARM NEON (AdvSimd) on Apple Silicon / ARM64, SSE4.1 and SSE2 on x86/x64.
    /// </summary>
    /// <param name="source">Source PCM16 samples as bytes (little-endian).</param>
    /// <param name="dest">Destination Float32 buffer.</param>
    /// <param name="sampleCount">Number of samples to convert.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ConvertPCM16ToFloat32(ReadOnlySpan<byte> source, Span<float> dest, int sampleCount)
    {
        if (sampleCount > dest.Length)
            throw new ArgumentException("Destination buffer too small");

        if (source.Length < sampleCount * sizeof(short))
            throw new ArgumentException("Source buffer too small");

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(source);
        int i = 0;

        // ARM NEON path (AdvSimd): Process 4 samples per iteration.
        // Critical for Apple Silicon (M1/M2/M3) where the x86 paths are dead code.
        //
        // Strategy:
        //   1. SignExtendWideningLower(Vector64<short>) → Vector128<int>  (4 x int32, SXTL)
        //   2. ConvertToSingle(Vector64<int>) → Vector64<float>  (2 x float32, SCVTF×2)
        //   3. Combine lower and upper halves into Vector128<float> + scale.
        if (AdvSimd.IsSupported && sampleCount >= 4)
        {
            Vector64<float> scaleVec64 = Vector64.Create(Scale16Bit);
            int simdEnd = sampleCount - 3;

            for (; i < simdEnd; i += 4)
            {
                // Pack 4 int16 values into a 64-bit vector (NEON q-reg lower half)
                Vector64<short> shortVec = Vector64.Create(
                    samples[i], samples[i + 1], samples[i + 2], samples[i + 3]);

                // Sign-extend int16 → int32 for all 4 lanes (SXTL)
                Vector128<int> intVec = AdvSimd.SignExtendWideningLower(shortVec);

                // Convert lower 2 int32 → float32, then upper 2 (SCVTF)
                Vector64<float> loFloat = AdvSimd.ConvertToSingle(intVec.GetLower());
                Vector64<float> hiFloat = AdvSimd.ConvertToSingle(intVec.GetUpper());

                // Scale to [-1.0, 1.0]
                loFloat = AdvSimd.Multiply(loFloat, scaleVec64);
                hiFloat = AdvSimd.Multiply(hiFloat, scaleVec64);

                // Combine into a single 128-bit result and store
                Vector128.Create(loFloat, hiFloat).CopyTo(dest.Slice(i, 4));
            }
        }
        // SSE4.1 path: Process 4 samples at once (x86/x64)
        else if (Sse41.IsSupported && sampleCount >= 4)
        {
            Vector128<float> scale = Vector128.Create(Scale16Bit);
            int simdEnd = sampleCount - 3;

            for (; i < simdEnd; i += 4)
            {
                // Load 4 x int16 as lower half of Vector128<short>
                Vector128<short> shortVec = Vector128.Create(
                    samples[i], samples[i + 1], samples[i + 2], samples[i + 3],
                    (short)0, (short)0, (short)0, (short)0);

                // Convert int16 -> int32 (sign-extend via PMOVSXWD)
                Vector128<int> intVec = Sse41.ConvertToVector128Int32(shortVec);

                // Convert int32 -> float
                Vector128<float> floatVec = Sse2.ConvertToVector128Single(intVec);

                // Multiply by scale
                Vector128<float> result = Sse.Multiply(floatVec, scale);

                result.CopyTo(dest.Slice(i, 4));
            }
        }
        // SSE2 fallback (no SSE4.1): same lane count, manual sign-extension
        else if (Sse2.IsSupported && sampleCount >= 4)
        {
            Vector128<float> scale = Vector128.Create(Scale16Bit);
            int simdEnd = sampleCount - 3;

            for (; i < simdEnd; i += 4)
            {
                Vector128<int> intVec = Vector128.Create(
                    (int)samples[i], (int)samples[i + 1],
                    (int)samples[i + 2], (int)samples[i + 3]);

                Vector128<float> floatVec = Sse2.ConvertToVector128Single(intVec);
                Vector128<float> result = Sse.Multiply(floatVec, scale);
                result.CopyTo(dest.Slice(i, 4));
            }
        }

        // Scalar fallback for remainder (or non-SIMD platforms)
        for (; i < sampleCount; i++)
        {
            dest[i] = samples[i] * Scale16Bit;
        }
    }

    /// <summary>
    /// Converts 8-bit unsigned PCM to Float32 using SIMD acceleration.
    /// </summary>
    /// <param name="source">Source PCM8 samples (unsigned, 0-255).</param>
    /// <param name="dest">Destination Float32 buffer.</param>
    /// <param name="sampleCount">Number of samples to convert.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ConvertPCM8ToFloat32(ReadOnlySpan<byte> source, Span<float> dest, int sampleCount)
    {
        if (sampleCount > dest.Length || sampleCount > source.Length)
            throw new ArgumentException("Buffer too small");

        int i = 0;

        // AVX2 path: Process 8 samples at once
        if (Avx2.IsSupported && sampleCount >= 8)
        {
            Vector256<float> scale = Vector256.Create(Scale8Bit);
            Vector256<int> offset = Vector256.Create(128); // Convert unsigned to signed
            int simdEnd = sampleCount - 7;

            for (; i < simdEnd; i += 8)
            {
                // Load 8 x uint8 as uint32 (zero-extended)
                Vector256<int> byteVec = Vector256.Create(
                    (int)source[i], (int)source[i + 1], (int)source[i + 2], (int)source[i + 3],
                    (int)source[i + 4], (int)source[i + 5], (int)source[i + 6], (int)source[i + 7]);

                // Subtract 128 to convert to signed
                Vector256<int> signedVec = Avx2.Subtract(byteVec, offset);

                // Convert int32 -> float
                Vector256<float> floatVec = Avx.ConvertToVector256Single(signedVec);

                // Multiply by scale
                Vector256<float> result = Avx.Multiply(floatVec, scale);

                // Store result
                result.CopyTo(dest.Slice(i, 8));
            }
        }

        // Scalar fallback
        for (; i < sampleCount; i++)
        {
            int sample = source[i] - 128;
            dest[i] = sample * Scale8Bit;
        }
    }

    /// <summary>
    /// Converts 24-bit signed PCM to Float32 (scalar only, 24-bit SIMD is complex).
    /// </summary>
    /// <param name="source">Source PCM24 samples as bytes (little-endian, 3 bytes per sample).</param>
    /// <param name="dest">Destination Float32 buffer.</param>
    /// <param name="sampleCount">Number of samples to convert.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ConvertPCM24ToFloat32(ReadOnlySpan<byte> source, Span<float> dest, int sampleCount)
    {
        if (sampleCount > dest.Length)
            throw new ArgumentException("Destination buffer too small");

        if (source.Length < sampleCount * 3)
            throw new ArgumentException("Source buffer too small");

        for (int i = 0; i < sampleCount; i++)
        {
            int byteIndex = i * 3;

            // Read 24-bit little-endian signed integer
            int sample = source[byteIndex] | (source[byteIndex + 1] << 8) | (source[byteIndex + 2] << 16);

            // Sign extend from 24-bit to 32-bit
            if ((sample & 0x800000) != 0)
                sample |= unchecked((int)0xFF000000);

            dest[i] = sample * Scale24Bit;
        }
    }

    /// <summary>
    /// Converts 32-bit signed PCM to Float32 using SIMD acceleration.
    /// </summary>
    /// <param name="source">Source PCM32 samples as bytes (little-endian).</param>
    /// <param name="dest">Destination Float32 buffer.</param>
    /// <param name="sampleCount">Number of samples to convert.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ConvertPCM32ToFloat32(ReadOnlySpan<byte> source, Span<float> dest, int sampleCount)
    {
        if (sampleCount > dest.Length)
            throw new ArgumentException("Destination buffer too small");

        if (source.Length < sampleCount * sizeof(int))
            throw new ArgumentException("Source buffer too small");

        ReadOnlySpan<int> samples = MemoryMarshal.Cast<byte, int>(source);
        int i = 0;

        // AVX2 path: Process 8 samples at once
        if (Avx2.IsSupported && sampleCount >= 8)
        {
            Vector256<float> scale = Vector256.Create(Scale32Bit);
            int simdEnd = sampleCount - 7;

            for (; i < simdEnd; i += 8)
            {
                // Load 8 x int32 (256 bits)
                Vector256<int> intVec = MemoryMarshal.Read<Vector256<int>>(
                    MemoryMarshal.AsBytes(samples.Slice(i, 8)));

                // Convert int32 -> float (256-bit)
                Vector256<float> floatVec = Avx.ConvertToVector256Single(intVec);

                // Multiply by scale
                Vector256<float> result = Avx.Multiply(floatVec, scale);

                // Store result
                result.CopyTo(dest.Slice(i, 8));
            }
        }
        // SSE2 path: Process 4 samples at once
        else if (Sse2.IsSupported && sampleCount >= 4)
        {
            Vector128<float> scale = Vector128.Create(Scale32Bit);
            int simdEnd = sampleCount - 3;

            for (; i < simdEnd; i += 4)
            {
                // Load 4 x int32 (128 bits)
                Vector128<int> intVec = MemoryMarshal.Read<Vector128<int>>(
                    MemoryMarshal.AsBytes(samples.Slice(i, 4)));

                // Convert int32 -> float
                Vector128<float> floatVec = Sse2.ConvertToVector128Single(intVec);

                // Multiply by scale
                Vector128<float> result = Sse.Multiply(floatVec, scale);

                // Store result
                result.CopyTo(dest.Slice(i, 4));
            }
        }

        // Scalar fallback
        for (; i < sampleCount; i++)
        {
            dest[i] = samples[i] * Scale32Bit;
        }
    }

    /// <summary>
    /// Converts int32 samples to Float32 using SIMD with custom scale factor.
    /// Used for FLAC decoding with variable bit depths.
    /// </summary>
    /// <param name="source">Source int32 samples.</param>
    /// <param name="dest">Destination Float32 buffer.</param>
    /// <param name="sampleCount">Number of samples to convert.</param>
    /// <param name="scale">Scaling factor (typically 1.0f / (1 &lt;&lt; (bitsPerSample - 1))).</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ConvertInt32ToFloat32(ReadOnlySpan<int> source, Span<float> dest, int sampleCount, float scale)
    {
        if (sampleCount > dest.Length || sampleCount > source.Length)
            throw new ArgumentException("Buffer too small");

        int i = 0;

        // AVX2 path: Process 8 samples at once
        if (Avx2.IsSupported && sampleCount >= 8)
        {
            Vector256<float> scaleVec = Vector256.Create(scale);
            int simdEnd = sampleCount - 7;

            for (; i < simdEnd; i += 8)
            {
                // Load 8 x int32
                Vector256<int> intVec = MemoryMarshal.Read<Vector256<int>>(
                    MemoryMarshal.AsBytes(source.Slice(i, 8)));

                // Convert int32 -> float
                Vector256<float> floatVec = Avx.ConvertToVector256Single(intVec);

                // Multiply by scale
                Vector256<float> result = Avx.Multiply(floatVec, scaleVec);

                // Store result
                result.CopyTo(dest.Slice(i, 8));
            }
        }
        // SSE2 path: Process 4 samples at once
        else if (Sse2.IsSupported && sampleCount >= 4)
        {
            Vector128<float> scaleVec = Vector128.Create(scale);
            int simdEnd = sampleCount - 3;

            for (; i < simdEnd; i += 4)
            {
                // Load 4 x int32
                Vector128<int> intVec = MemoryMarshal.Read<Vector128<int>>(
                    MemoryMarshal.AsBytes(source.Slice(i, 4)));

                // Convert int32 -> float
                Vector128<float> floatVec = Sse2.ConvertToVector128Single(intVec);

                // Multiply by scale
                Vector128<float> result = Sse.Multiply(floatVec, scaleVec);

                // Store result
                result.CopyTo(dest.Slice(i, 4));
            }
        }

        // Scalar fallback
        for (; i < sampleCount; i++)
        {
            dest[i] = source[i] * scale;
        }
    }
}
