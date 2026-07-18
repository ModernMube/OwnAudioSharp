using System;
using System.Runtime.CompilerServices;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Events;

namespace OwnaudioNET.Engine;

/// <summary>
/// Output circular buffer + input buffer pool behind the engine wrapper. Send path is lock-free and alloc-free.
/// </summary>
internal sealed class AudioBufferController : IDisposable
{
   private readonly CircularBuffer _outputBuffer;
   private readonly AudioBufferPool _inputBufferPool;
   private readonly int _engineBufferSize;
   private readonly int _channels;

   private long _totalUnderruns;
   private long _totalSamplesSent;
   private bool _disposed;

   /// <summary>
   /// Fires when the out buffer is full and we had to drop samples.
   /// </summary>
   public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

   /// <summary>
   /// Samples waiting in the output buffer.
   /// </summary>
   public int OutputBufferAvailable => _outputBuffer.Available;

   /// <summary>
   /// Output buffer size in samples.
   /// </summary>
   public int OutputBufferCapacity => _outputBuffer.Capacity;

   /// <summary>
   /// How many times we ran out of room so far.
   /// </summary>
   public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

   /// <summary>
   /// Sample count pushed through since construction.
   /// </summary>
   public long TotalSamplesSent => Interlocked.Read(ref _totalSamplesSent);

   /// <summary>
   /// Builds the circular buffer and the input pool. The multiplier is the headroom over one engine buffer.
   /// </summary>
   public AudioBufferController(int engineBufferSize, int channels, int bufferMultiplier = 8)
   {
      if (engineBufferSize <= 0)
         throw new ArgumentOutOfRangeException(nameof(engineBufferSize), "Engine buffer size must be positive.");
      if (channels <= 0)
         throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be positive.");
      if(bufferMultiplier <= 0)
         throw new ArgumentOutOfRangeException(nameof(bufferMultiplier), "Buffer multiplier must be positive.");

      _engineBufferSize = engineBufferSize;
      _channels = channels;

      _outputBuffer = new CircularBuffer(engineBufferSize * bufferMultiplier);
      _inputBufferPool = new AudioBufferPool(engineBufferSize, initialPoolSize: 4, maxPoolSize: 16);
   }

   /// <summary>
   /// Pushes interleaved float samples into the out buffer.
   /// </summary>
   /// <returns>How many samples actually landed there.</returns>
   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   public int Send(ReadOnlySpan<float> samples)
   {
      _throwIfDisposed();

      if (samples.IsEmpty) { return 0; }

      int written = _outputBuffer.Write(samples);
      Interlocked.Add(ref _totalSamplesSent, written);

      if (written < samples.Length)
      {
         int _dropped = (samples.Length - written) / _channels;
         Interlocked.Increment(ref _totalUnderruns);

         BufferUnderrun?.Invoke(this, new BufferUnderrunEventArgs(
             missedFrames: _dropped,
             position: Interlocked.Read(ref _totalSamplesSent) / _channels
         ));
      }

      return written;
   }

   /// <summary>
   /// Pulls samples out for the pump thread.
   /// </summary>
   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   public int Read(Span<float> buffer)
   {
      _throwIfDisposed();
      return _outputBuffer.Read(buffer);
   }

   /// <summary>
   /// Grabs a capture buffer from the pool.
   /// </summary>
   public float[]? RentInputBuffer()
   {
      _throwIfDisposed();
      return _inputBufferPool.Rent();
   }

   /// <summary>
   /// Hands a capture buffer back. Wrong sized or late buffers are just dropped.
   /// </summary>
   public void ReturnInputBuffer(float[] buffer)
   {
      if (_disposed || buffer == null || buffer.Length != _engineBufferSize) return;

      _inputBufferPool.Return(buffer);
   }

   /// <summary>
   /// Throws away everything still queued for output.
   /// </summary>
   public void ClearOutputBuffer()
   {
      _throwIfDisposed();
      _outputBuffer.Clear();
   }

   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   private void _throwIfDisposed()
   {
      if (_disposed)
         throw new ObjectDisposedException(nameof(AudioBufferController));
   }

   /// <summary>
   /// Drops the buffer content and the pool.
   /// </summary>
   public void Dispose()
   {
      if (_disposed)
         return;

      _outputBuffer.Clear();
      _inputBufferPool.Clear();

      _disposed = true;
   }

   /// <summary>
   /// State dump for logs.
   /// </summary>
   public override string ToString()
   {
      return $"AudioBufferController: OutputBuffer: {_outputBuffer.Available}/{_outputBuffer.Capacity} samples, " +
             $"Underruns: {TotalUnderruns}, TotalSent: {TotalSamplesSent} samples";
   }
}
