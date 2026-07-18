using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ownaudio.Core.Common;

/// <summary>
/// Keeps a handful of frames + their byte buffers around so the decoder loop
/// stops hammering the GC. Rent it, use it, hand it back.
/// </summary>
public sealed class AudioFramePool
{
    private readonly ConcurrentBag<PooledAudioFrame> _frames;
    private readonly int _bufferSize;
    private readonly int _maxPoolSize;
    private int _currentSize;

    /// <summary>
    /// bufferSize is per-frame bytes, maxPoolSize 0 means keep everything.
    /// </summary>
    /// <param name="bufferSize"></param>
    /// <param name="initialPoolSize"></param>
    /// <param name="maxPoolSize"></param>
    public AudioFramePool(int bufferSize, int initialPoolSize = 4, int maxPoolSize = 16)
    {
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));

        _bufferSize = bufferSize;
        _maxPoolSize = maxPoolSize;
        _frames = new ConcurrentBag<PooledAudioFrame>();

        for (int i = 0; i < initialPoolSize; i++)
        {
            _frames.Add(new PooledAudioFrame(new byte[bufferSize]));
            _currentSize++;
        }
    }

    /// <summary>
    /// Buffer size every frame in this pool carries.
    /// </summary>
    public int BufferSize => _bufferSize;

    /// <summary>
    /// Grabs a frame. Pool empty? We just make a fresh one.
    /// </summary>
    /// <returns>A frame stamped with the given time and length.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledAudioFrame Rent(double presentationTime, int dataLength)
    {
        if(dataLength > _bufferSize)
            throw new ArgumentException($"Data length {dataLength} exceeds buffer size {_bufferSize}");

        if (_frames.TryTake(out PooledAudioFrame frame))
        {
            Interlocked.Decrement(ref _currentSize);
            frame.Reset(presentationTime, dataLength);
            return frame;
        }

        return new PooledAudioFrame(new byte[_bufferSize], presentationTime, dataLength);
    }

    /// <summary>
    /// Hands a frame back. Wrong-sized or surplus frames are dropped on the floor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(PooledAudioFrame frame)
    {
        if (frame == null || frame.BufferCapacity != _bufferSize) return;

        int _newSize = Interlocked.Increment(ref _currentSize);
        if (_maxPoolSize > 0 && _newSize > _maxPoolSize)
        {
            Interlocked.Decrement(ref _currentSize);
            return;
        }

        _frames.Add(frame);
    }

    /// <summary>
    /// Drops everything we are holding.
    /// </summary>
    public void Clear()
    {
        while (_frames.TryTake(out _))
            Interlocked.Decrement(ref _currentSize);
    }
}

/// <summary>
/// A frame that sits on a reused buffer — spans in, no copies, no garbage.
/// </summary>
public sealed class PooledAudioFrame
{
    private readonly byte[] _buffer;
    private double _presentationTime;
    private int _dataLength;

    /// <summary>
    /// Presentation time in milliseconds.
    /// </summary>
    public double PresentationTime => _presentationTime;

    /// <summary>
    /// The live part of the buffer — this is what you read/write.
    /// </summary>
    public Span<byte> DataSpan => _buffer.AsSpan(0, _dataLength);

    /// <summary>
    /// Whole buffer, for when you are filling it up.
    /// </summary>
    public Span<byte> BufferSpan => _buffer.AsSpan();

    /// <summary></summary>
    public int DataLength => _dataLength;

    /// <summary>
    /// How much the buffer could hold.
    /// </summary>
    public int BufferCapacity => _buffer.Length;

    internal PooledAudioFrame(byte[] buffer, double presentationTime = 0.0, int dataLength = 0)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _presentationTime = presentationTime;
        _dataLength = dataLength;
    }

    /// <summary>
    /// Copies out into a plain AudioFrame. Allocates, so only where the API forces it.
    /// </summary>
    public AudioFrame ToAudioFrame()
    {
        byte[] _data = new byte[_dataLength];
        _buffer.AsSpan(0, _dataLength).CopyTo(_data);
        return new AudioFrame(_presentationTime, _data);
    }

    internal void Reset(double presentationTime, int dataLength)
    {
        _presentationTime = presentationTime;
        _dataLength = dataLength;
    }
}
