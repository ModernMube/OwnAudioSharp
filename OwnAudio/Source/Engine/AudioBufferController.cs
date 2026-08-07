using System;
using System.Runtime.CompilerServices;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Events;

namespace OwnaudioNET.Engine;

/// <summary>
/// Input buffer pool behind the engine wrapper. Output is queued straight into the engine's native
/// render ring these days, so nothing on the playback path lives here any more.
/// </summary>
internal sealed class AudioBufferController : IDisposable
{
   private readonly AudioBufferPool _inputBufferPool;
   private readonly int _engineBufferSize;

   private bool _disposed;

   /// <summary>
   /// Builds the input pool sized to one engine buffer.
   /// </summary>
   public AudioBufferController(int engineBufferSize, int channels)
   {
      if (engineBufferSize <= 0)
         throw new ArgumentOutOfRangeException(nameof(engineBufferSize), "Engine buffer size must be positive.");
      if (channels <= 0)
         throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be positive.");

      _engineBufferSize = engineBufferSize;
      _inputBufferPool = new AudioBufferPool(engineBufferSize, initialPoolSize: 4, maxPoolSize: 16);
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

      _inputBufferPool.Clear();

      _disposed = true;
   }

   /// <summary>
   /// State dump for logs.
   /// </summary>
   public override string ToString()
   {
      return $"AudioBufferController: input pool of {_engineBufferSize} sample buffers";
   }
}
