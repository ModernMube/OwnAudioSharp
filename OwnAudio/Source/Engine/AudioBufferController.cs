using System;
using System.Runtime.CompilerServices;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Events;

namespace OwnaudioNET.Engine;

/// <summary>
/// Manages audio buffer operations for input and output.
/// Provides lock-free circular buffering for output and pooled buffers for input.
/// </summary>
/// <remarks>
/// This class is responsible for:
/// - Managing the output circular buffer for Send operations
/// - Managing the input buffer pool for Receive operations
/// - Tracking buffer statistics (underruns, available space)
/// - Raising buffer-related events
/// 
/// Thread Safety: All public methods are thread-safe.
/// Performance: Send operations are lock-free and zero-allocation.
/// </remarks>
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
   /// Event raised when the output buffer is full and incoming audio is dropped.
   /// </summary>
   public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

   /// <summary>
   /// Gets the number of samples currently available in the output buffer.
   /// </summary>
   public int OutputBufferAvailable => _outputBuffer.Available;

   /// <summary>
   /// Gets the total capacity of the output buffer in samples.
   /// </summary>
   public int OutputBufferCapacity => _outputBuffer.Capacity;

   /// <summary>
   /// Gets the total number of buffer underrun events that have occurred.
   /// </summary>
   public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

   /// <summary>
   /// Gets the total number of samples sent through this controller.
   /// </summary>
   public long TotalSamplesSent => Interlocked.Read(ref _totalSamplesSent);

   /// <summary>
   /// Initializes a new instance of the AudioBufferController class.
   /// </summary>
   /// <param name="engineBufferSize">The engine buffer size in samples (frames * channels).</param>
   /// <param name="channels">The number of audio channels.</param>
   /// <param name="bufferMultiplier">Multiplier for the circular buffer size (default: 8x engine buffer).</param>
   /// <exception cref="ArgumentOutOfRangeException">Thrown if engineBufferSize or channels is less than or equal to zero.</exception>
   public AudioBufferController(int engineBufferSize, int channels, int bufferMultiplier = 8)
   {
      if (engineBufferSize <= 0)
         throw new ArgumentOutOfRangeException(nameof(engineBufferSize), "Engine buffer size must be positive.");
      if (channels <= 0)
         throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be positive.");
      if (bufferMultiplier <= 0)
         throw new ArgumentOutOfRangeException(nameof(bufferMultiplier), "Buffer multiplier must be positive.");

      _engineBufferSize = engineBufferSize;
      _channels = channels;

      // Create output circular buffer
      // Increased from 2x to 8x to accommodate large mixer buffers (4096 frames) and heavy DSP.
      int circularBufferSize = engineBufferSize * bufferMultiplier;
      _outputBuffer = new CircularBuffer(circularBufferSize);

      // Create input buffer pool
      _inputBufferPool = new AudioBufferPool(engineBufferSize, initialPoolSize: 4, maxPoolSize: 16);

      _totalUnderruns = 0;
      _totalSamplesSent = 0;
   }

   /// <summary>
   /// Sends audio samples to the output buffer in a zero-allocation manner.
   /// </summary>
   /// <param name="samples">Audio samples in Float32 format, interleaved (e.g., L R L R for stereo).</param>
   /// <returns>The number of samples actually written to the buffer.</returns>
   /// <exception cref="ObjectDisposedException">Thrown if the controller has been disposed.</exception>
   /// </remarks>
   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   public int Send(ReadOnlySpan<float> samples)
   {
      ThrowIfDisposed();

      if (samples.IsEmpty)
         return 0;

      int written = _outputBuffer.Write(samples);

      Interlocked.Add(ref _totalSamplesSent, written);

      if (written < samples.Length)
      {
         int droppedSamples = samples.Length - written;
         int droppedFrames = droppedSamples / _channels;

         Interlocked.Increment(ref _totalUnderruns);

         BufferUnderrun?.Invoke(this, new BufferUnderrunEventArgs(
             missedFrames: droppedFrames,
             position: Interlocked.Read(ref _totalSamplesSent) / _channels
         ));
      }

      return written;
   }

   /// <summary>
   /// Reads audio samples from the output buffer.
   /// Used by the pump thread to retrieve data for sending to the engine.
   /// </summary>
   /// <param name="buffer">The buffer to read samples into.</param>
   /// <returns>The number of samples actually read from the buffer.</returns>
   /// <exception cref="ObjectDisposedException">Thrown if the controller has been disposed.</exception>
   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   public int Read(Span<float> buffer)
   {
      ThrowIfDisposed();
      return _outputBuffer.Read(buffer);
   }

   /// <summary>
   /// Returns an input buffer from the pool.
   /// Used when receiving audio from the engine.
   /// </summary>
   /// <returns>A buffer from the pool, or null if the pool is empty.</returns>
   /// <exception cref="ObjectDisposedException">Thrown if the controller has been disposed.</exception>
   public float[]? RentInputBuffer()
   {
      ThrowIfDisposed();
      return _inputBufferPool.Rent();
   }

   /// <summary>
   /// Returns an input buffer to the pool for reuse.
   /// </summary>
   /// <param name="buffer">The buffer to return.</param>
   /// <exception cref="ObjectDisposedException">Thrown if the controller has been disposed.</exception>
   public void ReturnInputBuffer(float[] buffer)
   {
      if (buffer == null || buffer.Length != _engineBufferSize)
         return; // Invalid buffer - discard

      if (_disposed)
         return; // Already disposed, don't throw

      try
      {
         _inputBufferPool.Return(buffer);
      }
      catch {}
   }

   /// <summary>
   /// Clears the output buffer, discarding all pending audio data.
   /// </summary>
   /// <exception cref="ObjectDisposedException">Thrown if the controller has been disposed.</exception>
   public void ClearOutputBuffer()
   {
      ThrowIfDisposed();
      _outputBuffer.Clear();
   }

   /// <summary>
   /// Throws ObjectDisposedException if the controller has been disposed.
   /// </summary>
   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   private void ThrowIfDisposed()
   {
      if (_disposed)
         throw new ObjectDisposedException(nameof(AudioBufferController));
   }

   /// <summary>
   /// Disposes the audio buffer controller and releases all resources.
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
   /// Returns a string representation of the controller's current state.
   /// </summary>
   public override string ToString()
   {
      return $"AudioBufferController: OutputBuffer: {_outputBuffer.Available}/{_outputBuffer.Capacity} samples, " +
             $"Underruns: {TotalUnderruns}, TotalSent: {TotalSamplesSent} samples";
   }
}
