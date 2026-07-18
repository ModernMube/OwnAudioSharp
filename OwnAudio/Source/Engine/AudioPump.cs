using System;
using System.Threading;
using Ownaudio.Core;

namespace OwnaudioNET.Engine;

/// <summary>
/// High priority background thread that keeps shovelling samples from the buffer controller into the engine.
/// </summary>
internal sealed class AudioPump : IDisposable
{
   /// <summary>
   /// How often a repeating loop error gets re-logged.
   /// </summary>
   private const int PumpErrorReportInterval = 1000;

   /// <summary>
   /// Consecutive errors after which we call it persistent and give up.
   /// </summary>
   private const int PumpErrorFaultThreshold = 500;

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
   /// True while the pump thread is alive.
   /// </summary>
   public bool IsRunning => _isRunning;

   /// <summary>
   /// Frame count handed to the engine so far.
   /// </summary>
   public long TotalPumpedFrames => Interlocked.Read(ref _pumpedFrames);

   /// <summary>
   /// Sets up the pump. Sleep interval comes out of half a buffer worth of time at the given sample rate.
   /// </summary>
   /// <param name="engine"></param>
   /// <param name="bufferController"></param>
   /// <param name="engineBufferSize"></param>
   /// <param name="framesPerBuffer"></param>
   /// <param name="sampleRate"></param>
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
      if(sampleRate <= 0)
         throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");

      _engineBufferSize = engineBufferSize;
      _framesPerBuffer = framesPerBuffer;

      double _halfBufferMs = (framesPerBuffer / 2.0) / sampleRate * 1000.0;
      _sleepIntervalMs = Math.Max(1, (int)Math.Round(_halfBufferMs));
   }

   /// <summary>
   /// Spins up the pump thread. Idempotent.
   /// </summary>
   public void Start()
   {
      _throwIfDisposed();

      if (_isRunning) return;

      _stopRequested = false;
      _isRunning = true;

      _pumpThread = new Thread(_pumpLoop)
      {
         Name = "AudioPump.PumpThread",
         IsBackground = true,
         Priority = ThreadPriority.Highest
      };
      _pumpThread.Start();
   }

   /// <summary>
   /// Signals the loop and waits for it to leave. Blocks up to timeoutMs, so call it off the UI thread.
   /// </summary>
   /// <param name="timeoutMs"></param>
   public void Stop(int timeoutMs = 2000)
   {
      _throwIfDisposed();

      if (!_isRunning) return;

      _stopRequested = true;

      // We wait for the loop to actually exit, the caller usually disposes the engine right after us
      // and a live loop still calling Send() into it is a use-after-dispose race. Never Abort().
      Thread? _thread = _pumpThread;
      if (_thread is not null && _thread != Thread.CurrentThread && _thread.IsAlive)
         _thread.Join(TimeSpan.FromMilliseconds(timeoutMs));

      _pumpThread = null;
      _isRunning = false;
   }

   /// <summary>
   /// Stop on a background thread so the UI does not freeze.
   /// </summary>
   /// <param name="timeoutMs"></param>
   /// <param name="cancellationToken"></param>
   /// <returns></returns>
   public async Task StopAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
   {
      await Task.Run(() =>
      {
         cancellationToken.ThrowIfCancellationRequested();
         Stop(timeoutMs);
      }, cancellationToken).ConfigureAwait(false);
   }

   /// <summary>
   /// The loop itself, reads a buffer worth and pushes it to the engine, otherwise naps.
   /// </summary>
   private void _pumpLoop()
   {
      float[] _temp = new float[_engineBufferSize];
      int _errors = 0;

      while (!_stopRequested)
      {
         try
         {
            if (_bufferController.OutputBufferAvailable >= _engineBufferSize)
            {
               Span<float> _span = _temp.AsSpan();
               int _read = _bufferController.Read(_span);

               if (_read == _engineBufferSize)
               {
                  _engine.Send(_span);
                  Interlocked.Add(ref _pumpedFrames, _framesPerBuffer);
               }
               else if (_read > 0)
               {
                  _engine.Send(_span.Slice(0, _read));
                  Interlocked.Add(ref _pumpedFrames, _read / (_engineBufferSize / _framesPerBuffer));
               }
            }
            else
            {
               Thread.Sleep(_sleepIntervalMs);
            }

            _errors = 0;
         }
         catch (Exception ex)
         {
            _errors++;

            // Don't swallow it silently, a stuck fault would otherwise throw hundreds of times a second.
            if (_errors == 1 || _errors % PumpErrorReportInterval == 0)
               Console.Error.WriteLine($"[AudioPump] Send loop error (occurrence #{_errors}): {ex.Message}");

            if (_errors >= PumpErrorFaultThreshold)
            {
               _stopRequested = true;
               _isRunning = false;
               break;
            }

            Thread.Sleep(_sleepIntervalMs * 2);
         }
      }
   }

   /// <summary>
   /// Guard for calls after dispose.
   /// </summary>
   private void _throwIfDisposed()
   {
      if (_disposed)
         throw new ObjectDisposedException(nameof(AudioPump));
   }

   /// <summary>
   /// Stops the thread and marks us dead.
   /// </summary>
   public void Dispose()
   {
      if (_disposed)
         return;

      if (_isRunning)
      {
         try { Stop(); }
         catch {}
      }

      _disposed = true;
   }

   /// <summary>
   /// Short state dump for logs.
   /// </summary>
   public override string ToString()
   {
      return $"AudioPump: Running: {_isRunning}, Pumped: {TotalPumpedFrames} frames, SleepInterval: {_sleepIntervalMs}ms";
   }
}
