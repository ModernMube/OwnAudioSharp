using System;
using System.Threading;
using Ownaudio.Core;

namespace OwnaudioNET.Engine;

/// <summary>
/// Manages the audio pump thread that transfers data from the buffer to the audio engine.
/// Provides high-priority background thread for continuous audio streaming.
/// </summary>
/// <remarks>
/// This class is responsible for:
/// - Managing the pump thread lifecycle (start, stop, restart)
/// - Reading from the buffer controller and sending to the engine
/// - Tracking pumping statistics (frames pumped)
/// - Handling thread synchronization and graceful shutdown
/// 
/// Thread Safety: All public methods are thread-safe.
/// Performance: Runs at ThreadPriority.Highest to minimize audio glitches.
/// </remarks>
internal sealed class AudioPump : IDisposable
{
   private readonly IAudioEngine _engine;
   private readonly AudioBufferController _bufferController;
   private readonly int _engineBufferSize;
   private readonly int _framesPerBuffer;
   private readonly int _sleepIntervalMs;

   private Thread? _pumpThread;
   private volatile bool _stopRequested;
   private volatile bool _isRunning;
   private long _pumpedFrames;
   private bool _disposed;

   /// <summary>
   /// Gets whether the pump thread is currently running.
   /// </summary>
   public bool IsRunning => _isRunning;

   /// <summary>
   /// Gets the total number of frames pumped to the audio engine.
   /// </summary>
   public long TotalPumpedFrames => Interlocked.Read(ref _pumpedFrames);

   /// <summary>
   /// Initializes a new instance of the AudioPump class.
   /// </summary>
   /// <param name="engine">The audio engine to pump data to.</param>
   /// <param name="bufferController">The buffer controller to read data from.</param>
   /// <param name="engineBufferSize">The engine buffer size in samples (frames * channels).</param>
   /// <param name="framesPerBuffer">The number of frames per buffer.</param>
   /// <param name="sampleRate">The audio sample rate in Hz.</param>
   /// <exception cref="ArgumentNullException">Thrown if engine or bufferController is null.</exception>
   /// <exception cref="ArgumentOutOfRangeException">Thrown if engineBufferSize, framesPerBuffer, or sampleRate is invalid.</exception>
   public AudioPump(
       IAudioEngine engine,
       AudioBufferController bufferController,
       int engineBufferSize,
       int framesPerBuffer,
       int sampleRate)
   {
      _engine = engine ?? throw new ArgumentNullException(nameof(engine));
      _bufferController = bufferController ?? throw new ArgumentNullException(nameof(bufferController));

      if (engineBufferSize <= 0)
         throw new ArgumentOutOfRangeException(nameof(engineBufferSize), "Engine buffer size must be positive.");
      if (framesPerBuffer <= 0)
         throw new ArgumentOutOfRangeException(nameof(framesPerBuffer), "Frames per buffer must be positive.");
      if (sampleRate <= 0)
         throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");

      _engineBufferSize = engineBufferSize;
      _framesPerBuffer = framesPerBuffer;

      // Calculate sleep interval for pump thread (half buffer time to avoid tight loop)
      // Sleep time = (FramesPerBuffer / 2) / SampleRate * 1000 ms
      double halfBufferTimeMs = (framesPerBuffer / 2.0) / sampleRate * 1000.0;
      _sleepIntervalMs = Math.Max(1, (int)Math.Round(halfBufferTimeMs));

      _pumpedFrames = 0;
      _stopRequested = false;
      _isRunning = false;
   }

   /// <summary>
   /// Starts the pump thread.
   /// This method is thread-safe and idempotent.
   /// </summary>
   /// <exception cref="ObjectDisposedException">Thrown if the pump has been disposed.</exception>
   public void Start()
   {
      ThrowIfDisposed();

      if (_isRunning)
         return; // Already running

      _stopRequested = false;
      _isRunning = true;

      // Create new thread for each start (threads cannot be restarted)
      _pumpThread = new Thread(PumpThreadLoop)
      {
         Name = "AudioPump.PumpThread",
         IsBackground = true,
         Priority = ThreadPriority.Highest // High priority for audio pumping
      };
      _pumpThread.Start();
   }

   /// <summary>
   /// Stops the pump thread gracefully.
   /// This method is thread-safe and idempotent.
   /// </summary>
   /// <param name="timeoutMs">Maximum time to wait for the thread to exit, in milliseconds (default: 2000ms).</param>
   /// <exception cref="ObjectDisposedException">Thrown if the pump has been disposed.</exception>
   /// <remarks>
   /// WARNING: This method BLOCKS for up to the specified timeout waiting for the pump thread to exit.
   /// For UI applications, consider using StopAsync() instead to prevent UI freezing.
   /// </remarks>
   public void Stop(int timeoutMs = 2000)
   {
      ThrowIfDisposed();

      if (!_isRunning)
         return; // Already stopped

      // Signal pump thread to stop
      _stopRequested = true;

      // Wait for pump thread to exit (with timeout)
      if (_pumpThread != null && _pumpThread.IsAlive)
      {
         if (!_pumpThread.Join(TimeSpan.FromMilliseconds(timeoutMs)))
         {
            // Thread didn't exit gracefully - log warning but continue
            // Don't abort - it's unsafe in modern .NET
         }
      }

      _isRunning = false;
   }

   /// <summary>
   /// Stops the pump thread asynchronously.
   /// This method prevents UI thread blocking by running the stop operation on a background thread.
   /// </summary>
   /// <param name="timeoutMs">Maximum time to wait for the thread to exit, in milliseconds (default: 2000ms).</param>
   /// <param name="cancellationToken">Cancellation token to abort the wait (not the stop itself).</param>
   /// <exception cref="ObjectDisposedException">Thrown if the pump has been disposed.</exception>
   /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
   /// <remarks>
   /// Recommended for UI applications (WPF, WinForms, MAUI, Avalonia).
   /// Note: Even if cancelled, the pump will still attempt to stop gracefully.
   /// The cancellation only affects the async wait, not the stop operation itself.
   /// </remarks>
   public async Task StopAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
   {
      await Task.Run(() =>
      {
         cancellationToken.ThrowIfCancellationRequested();
         Stop(timeoutMs);
      }, cancellationToken).ConfigureAwait(false);
   }

   /// <summary>
   /// Pump thread loop - reads from buffer and sends to engine.
   /// This runs at high priority to minimize audio glitches.
   /// </summary>
   private void PumpThreadLoop()
   {
      // Pre-allocate buffer OUTSIDE loop to avoid stack overflow issues
      float[] tempBuffer = new float[_engineBufferSize];

      while (!_stopRequested)
      {
         try
         {
            // Check if enough data is available in the buffer
            int available = _bufferController.OutputBufferAvailable;

            if (available >= _engineBufferSize)
            {
               // Read from buffer into pre-allocated temp buffer
               Span<float> bufferSpan = tempBuffer.AsSpan();
               int read = _bufferController.Read(bufferSpan);

               if (read == _engineBufferSize)
               {
                  // Send to engine (this may block until hardware buffer has space)
                  _engine.Send(bufferSpan);

                  // Update statistics
                  Interlocked.Add(ref _pumpedFrames, _framesPerBuffer);
               }
               else if (read > 0)
               {
                  // Partial read - unusual, but handle gracefully
                  // Send what we have
                  _engine.Send(bufferSpan.Slice(0, read));
                  Interlocked.Add(ref _pumpedFrames, read / (_engineBufferSize / _framesPerBuffer));
               }
            }
            else
            {
               // Not enough data available - sleep and retry
               // This is normal during startup or sparse audio playback
               Thread.Sleep(_sleepIntervalMs);
            }
         }
         catch
         {
            // Error in pump thread - log but don't crash the thread
            // In production, log via ILogger
            // Back off on error to avoid tight loop
            Thread.Sleep(_sleepIntervalMs * 2);
         }
      }
   }

   /// <summary>
   /// Throws ObjectDisposedException if the pump has been disposed.
   /// </summary>
   private void ThrowIfDisposed()
   {
      if (_disposed)
         throw new ObjectDisposedException(nameof(AudioPump));
   }

   /// <summary>
   /// Disposes the audio pump and releases all resources.
   /// </summary>
   public void Dispose()
   {
      if (_disposed)
         return;

      // Stop pump if running
      if (_isRunning)
      {
         try
         {
            Stop();
         }
         catch
         {
            // Ignore errors during disposal
         }
      }

      _disposed = true;
   }

   /// <summary>
   /// Returns a string representation of the pump's current state.
   /// </summary>
   public override string ToString()
   {
      return $"AudioPump: Running: {_isRunning}, Pumped: {TotalPumpedFrames} frames, SleepInterval: {_sleepIntervalMs}ms";
   }
}
