using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Core.Common;

/// <summary>
/// Mutable wrapper for audio frame data used during decode loops.
/// Designed for zero-allocation reuse within decoder implementations.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> NOT thread-safe. Should only be used by a single decoder instance.</para>
/// <para><b>GC Behavior:</b> Zero-allocation after initial buffer allocation (reuses internal buffer).</para>
/// <para><b>Usage:</b> Internal to decoders. Use <see cref="ToImmutable"/> to create public-facing AudioFrame.</para>
/// </remarks>
internal sealed class MutableAudioFrame
{
    private float[] _data;
    private int _length;

    /// <summary>
    /// Presentation timestamp in milliseconds.
    /// </summary>
    public double PresentationTime { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MutableAudioFrame"/> class.
    /// </summary>
    /// <param name="initialCapacity">Initial buffer capacity in samples (default: 4096).</param>
    public MutableAudioFrame(int initialCapacity = 4096)
    {
        _data = new float[initialCapacity];
        _length = 0;
        PresentationTime = 0.0;
    }

    /// <summary>
    /// Resets the frame with new data from byte array.
    /// </summary>
    /// <param name="pts">Presentation timestamp in milliseconds.</param>
    /// <param name="newData">New frame data (will be copied).</param>
    /// <remarks>
    /// ZERO-ALLOCATION: Reuses internal buffer if large enough, only allocates if buffer needs to grow.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(double pts, ReadOnlySpan<byte> newData)
    {
        int requiredLength = newData.Length / sizeof(float);

        // Resize buffer if needed
        if (_data.Length < requiredLength)
        {
            Array.Resize(ref _data, requiredLength);
        }

        // Copy data as floats
        ReadOnlySpan<float> floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(newData);
        floatSpan.CopyTo(_data.AsSpan(0, requiredLength));

        _length = requiredLength;
        PresentationTime = pts;
    }

    /// <summary>
    /// Resets the frame with new float data.
    /// </summary>
    /// <param name="pts">Presentation timestamp in milliseconds.</param>
    /// <param name="newData">New frame data (will be copied).</param>
    /// <remarks>
    /// ZERO-ALLOCATION: Reuses internal buffer if large enough, only allocates if buffer needs to grow.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(double pts, ReadOnlySpan<float> newData)
    {
        // Resize buffer if needed
        if (_data.Length < newData.Length)
        {
            Array.Resize(ref _data, newData.Length);
        }

        // Copy data
        newData.CopyTo(_data.AsSpan(0, newData.Length));

        _length = newData.Length;
        PresentationTime = pts;
    }

    /// <summary>
    /// Resets the frame with data from AudioBuffer.
    /// </summary>
    /// <param name="pts">Presentation timestamp in milliseconds.</param>
    /// <param name="buffer">Source audio buffer.</param>
    /// <param name="lengthInSamples">Number of float samples to copy.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(double pts, AudioBuffer buffer, int lengthInSamples)
    {
        var floatSpan = buffer.AsSpan<float>();

        if (lengthInSamples > floatSpan.Length)
            throw new ArgumentException("Length exceeds buffer size", nameof(lengthInSamples));

        // Resize if needed
        if (_data.Length < lengthInSamples)
        {
            Array.Resize(ref _data, lengthInSamples);
        }

        // Copy from AudioBuffer
        floatSpan.Slice(0, lengthInSamples).CopyTo(_data);

        _length = lengthInSamples;
        PresentationTime = pts;
    }

    /// <summary>
    /// Creates an immutable AudioFrame from this mutable frame.
    /// </summary>
    /// <returns>Immutable AudioFrame with exact-sized data array.</returns>
    /// <remarks>
    /// ALLOCATION: This method allocates a new byte array for the immutable frame.
    /// Only call this when returning data to public API.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AudioFrame ToImmutable()
    {
        // Create exact-sized byte array
        byte[] byteData = new byte[_length * sizeof(float)];

        // Copy float data to byte array
        var floatSpan = _data.AsSpan(0, _length);
        var byteSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(floatSpan);
        byteSpan.CopyTo(byteData);

        return new AudioFrame(PresentationTime, byteData);
    }

    /// <summary>
    /// Gets a read-only span of the current frame data.
    /// </summary>
    /// <returns>Read-only span containing valid frame data.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<float> AsSpan()
    {
        return _data.AsSpan(0, _length);
    }

    /// <summary>
    /// Gets a writable span of the current frame data.
    /// </summary>
    /// <returns>Writable span containing valid frame data.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<float> AsWritableSpan()
    {
        return _data.AsSpan(0, _length);
    }

    /// <summary>
    /// Gets the current length of valid data in samples.
    /// </summary>
    public int Length => _length;

    /// <summary>
    /// Gets the current buffer capacity in samples.
    /// </summary>
    public int Capacity => _data.Length;

    /// <summary>
    /// Clears the frame data (sets length to zero).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        _length = 0;
        PresentationTime = 0.0;
    }

    /// <summary>
    /// Ensures the internal buffer has at least the specified capacity.
    /// </summary>
    /// <param name="minimumCapacity">Minimum required capacity in samples.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureCapacity(int minimumCapacity)
    {
        if (_data.Length < minimumCapacity)
        {
            Array.Resize(ref _data, minimumCapacity);
        }
    }
}
