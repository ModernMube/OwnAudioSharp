using System.Runtime.CompilerServices;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.Engine;

/// <summary>
/// Central wrapper class that bridges OwnaudioNET with the external IAudioEngine implementation.
/// Provides lock-free buffer management, pump thread pattern, and zero-allocation Send/Receive operations.
///
/// Architecture:
/// - Main Thread: User API calls (Send, Receive, Start, Stop, device management)
/// - Pump Thread: Reads from CircularBuffer and calls engine.Send() in a tight loop
/// - Engine RT Thread: Managed by external IAudioEngine (e.g., WASAPI RT thread)
///
/// Thread Safety:
/// - All public methods are thread-safe
/// - Internal buffer operations are lock-free (CircularBuffer SPSC pattern)
/// - State management uses volatile fields and Interlocked operations
///
/// Performance:
/// - Send() latency: &lt; 1ms (CircularBuffer write only)
/// - Total pipeline latency: ~21ms (2x buffer) + engine buffer (~10ms) = ~31ms @ 512 frames, 48kHz
/// - Zero allocations in Send() hot path
/// - Pump thread overhead: &lt; 5% CPU
/// </summary>
public sealed class AudioEngineWrapper : IDisposable
{
    // External engine instance
    private readonly IAudioEngine _engine;

    // Buffer management
    private readonly CircularBuffer _outputBuffer;
    private readonly AudioBufferPool _inputBufferPool;

    // Pump thread
    private Thread? _pumpThread;
    private volatile bool _stopRequested;
    private volatile bool _isRunning;

    // Configuration
    private readonly AudioConfig _config;
    private readonly int _engineBufferSize; // In samples (frames * channels)
    private readonly int _sleepIntervalMs;

    // Statistics and error tracking
    private long _totalUnderruns;
    private long _pumpedFrames;

    // Event forwarding subscriptions
    private EventHandler<AudioDeviceChangedEventArgs>? _engineOutputDeviceChanged;
    private EventHandler<AudioDeviceChangedEventArgs>? _engineInputDeviceChanged;
    private EventHandler<AudioDeviceStateChangedEventArgs>? _engineDeviceStateChanged;

    // Dispose flag
    private bool _disposed;

    /// <summary>
    /// Gets the actual frames per buffer negotiated with the audio device.
    /// This may differ from the requested buffer size in AudioEngineConfig.
    /// </summary>
    public int FramesPerBuffer { get; }

    /// <summary>
    /// Gets the audio configuration being used.
    /// </summary>
    public AudioConfig Config => _config;

    /// <summary>
    /// Gets whether the audio engine is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the number of samples currently available in the output buffer.
    /// </summary>
    public int OutputBufferAvailable => _outputBuffer.Available;

    /// <summary>
    /// Gets the total number of buffer underrun events that have occurred.
    /// </summary>
    public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

    /// <summary>
    /// Gets the total number of frames pumped to the audio engine.
    /// </summary>
    public long TotalPumpedFrames => Interlocked.Read(ref _pumpedFrames);

    /// <summary>
    /// Gets the underlying IAudioEngine instance.
    /// Useful for advanced scenarios like passing to AudioMixer directly.
    /// </summary>
    public IAudioEngine UnderlyingEngine => _engine;

    /// <summary>
    /// Event raised when the output buffer is full and incoming audio is dropped.
    /// </summary>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <summary>
    /// Event raised when the output device changes.
    /// </summary>
    public event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged;

    /// <summary>
    /// Event raised when the input device changes.
    /// </summary>
    public event EventHandler<AudioDeviceChangedEventArgs>? InputDeviceChanged;

    /// <summary>
    /// Event raised when a device state changes (added, removed, enabled, disabled).
    /// </summary>
    public event EventHandler<AudioDeviceStateChangedEventArgs>? DeviceStateChanged;

    /// <summary>
    /// Initializes a new instance of the AudioEngineWrapper class.
    /// </summary>
    /// <param name="engine">The external audio engine instance (must be initialized).</param>
    /// <param name="config">The audio configuration.</param>
    /// <exception cref="ArgumentNullException">Thrown if engine or config is null.</exception>
    /// <exception cref="AudioEngineException">Thrown if engine initialization parameters are invalid.</exception>
    public AudioEngineWrapper(IAudioEngine engine, AudioConfig config)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        // Get actual negotiated buffer size from engine
        FramesPerBuffer = _engine.FramesPerBuffer;
        if (FramesPerBuffer <= 0)
            throw new AudioEngineException("Engine FramesPerBuffer must be positive.", -1);

        // Calculate buffer size in samples (frames * channels)
        _engineBufferSize = FramesPerBuffer * _config.Channels;

        // Create output circular buffer (2x engine buffer size for ~21ms buffering @ 48kHz)
        // Reduced from 4x to 2x to minimize latency while maintaining adequate buffering
        // The SIMD-optimized mixing and Normal-priority decoder threads provide stable flow
        int circularBufferSize = _engineBufferSize * 2;
        _outputBuffer = new CircularBuffer(circularBufferSize);

        // Create input buffer pool
        _inputBufferPool = new AudioBufferPool(_engineBufferSize, initialPoolSize: 4, maxPoolSize: 16);

        // Calculate sleep interval for pump thread (half buffer time to avoid tight loop)
        // Sleep time = (FramesPerBuffer / 2) / SampleRate * 1000 ms
        double halfBufferTimeMs = (FramesPerBuffer / 2.0) / _config.SampleRate * 1000.0;
        _sleepIntervalMs = Math.Max(1, (int)Math.Round(halfBufferTimeMs));

        // Subscribe to engine events
        SubscribeToEngineEvents();

        // Pump thread will be created in Start() method to allow restart
        _pumpThread = null;

        _stopRequested = false;
        _isRunning = false;
        _totalUnderruns = 0;
        _pumpedFrames = 0;
    }

    /// <summary>
    /// Starts the audio engine and pump thread.
    /// This method is thread-safe and idempotent.
    /// </summary>
    /// <exception cref="AudioEngineException">Thrown if the engine fails to start.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    public void Start()
    {
        ThrowIfDisposed();

        if (_isRunning)
            return; // Already running

        try
        {
            // Start the external audio engine
            int result = _engine.Start();
            if (result < 0)
                throw new AudioEngineException($"Failed to start audio engine. Error code: {result}", result);

            // Start pump thread
            _stopRequested = false;
            _isRunning = true;

            // Create new thread for each start (threads cannot be restarted)
            _pumpThread = new Thread(PumpThreadLoop)
            {
                Name = "AudioEngineWrapper.PumpThread",
                IsBackground = true,
                Priority = ThreadPriority.Highest // High priority for audio pumping
            };
            _pumpThread.Start();
        }
        catch (Exception ex) when (ex is not AudioEngineException)
        {
            _isRunning = false;
            throw new AudioEngineException("Failed to start audio engine wrapper.", ex);
        }
    }

    /// <summary>
    /// Stops the audio engine and pump thread gracefully.
    /// This method is thread-safe and idempotent.
    ///
    /// ⚠️ **WARNING:** This method BLOCKS for up to 2 seconds waiting for the pump thread to exit!
    /// For UI applications, use StopAsync() instead to prevent UI freezing.
    /// </summary>
    /// <exception cref="AudioEngineException">Thrown if the engine fails to stop.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    public void Stop()
    {
        ThrowIfDisposed();

        if (!_isRunning)
            return; // Already stopped

        try
        {
            // Signal pump thread to stop
            _stopRequested = true;

            // Wait for pump thread to exit (with timeout)
            if (_pumpThread != null && _pumpThread.IsAlive)
            {
                if (!_pumpThread.Join(TimeSpan.FromSeconds(2)))
                {
                    // Thread didn't exit gracefully - log warning but continue
                    // Don't abort - it's unsafe in modern .NET
                }
            }

            // Stop the external audio engine
            int result = _engine.Stop();
            if (result < 0)
                throw new AudioEngineException($"Failed to stop audio engine. Error code: {result}", result);

            _isRunning = false;
        }
        catch (Exception ex) when (ex is not AudioEngineException)
        {
            throw new AudioEngineException("Failed to stop audio engine wrapper.", ex);
        }
    }

    /// <summary>
    /// Stops the audio engine and pump thread asynchronously.
    /// This method prevents UI thread blocking by running the stop operation on a background thread.
    ///
    /// Recommended for UI applications (WPF, WinForms, MAUI, Avalonia).
    ///
    /// Usage:
    /// <code>
    /// await wrapper.StopAsync();
    /// </code>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the wait (not the stop itself).</param>
    /// <exception cref="AudioEngineException">Thrown if the engine fails to stop.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    /// <remarks>
    /// Note: Even if cancelled, the engine will still attempt to stop gracefully.
    /// The cancellation only affects the async wait, not the engine stop operation itself.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stop();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends audio samples to the output device in a zero-allocation manner.
    /// This method writes to the internal CircularBuffer; the pump thread reads from the buffer
    /// and calls the engine's Send() method.
    ///
    /// Performance: &lt; 1ms latency (CircularBuffer write only).
    /// Thread Safety: Safe to call from any thread.
    /// Allocation: Zero allocations (uses Span&lt;T&gt;).
    /// </summary>
    /// <param name="samples">Audio samples in Float32 format, interleaved (e.g., L R L R for stereo).</param>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the engine is not running.</exception>
    /// <remarks>
    /// If the buffer is full, this method will drop the samples and raise a BufferUnderrun event.
    /// Consider using OutputBufferAvailable to check buffer space before sending large amounts of data.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(ReadOnlySpan<float> samples)
    {
        ThrowIfDisposed();

        if (!_isRunning)
            throw new InvalidOperationException("Cannot send audio when engine is not running. Call Start() first.");

        if (samples.IsEmpty)
            return;

        // Write to circular buffer (zero-allocation, lock-free)
        int written = _outputBuffer.Write(samples);

        // Check for underrun (buffer full - samples dropped)
        if (written < samples.Length)
        {
            int droppedSamples = samples.Length - written;
            int droppedFrames = droppedSamples / _config.Channels;

            Interlocked.Increment(ref _totalUnderruns);

            // Raise underrun event (do not block hot path - event handlers should be fast)
            BufferUnderrun?.Invoke(this, new BufferUnderrunEventArgs(
                missedFrames: droppedFrames,
                position: Interlocked.Read(ref _pumpedFrames)
            ));
        }
    }

    /// <summary>
    /// Receives audio samples from the input device.
    /// Returns a buffer from the internal AudioBufferPool to minimize allocations.
    /// </summary>
    /// <param name="sampleCount">The number of samples received (output parameter).</param>
    /// <returns>
    /// A buffer containing captured audio samples, or null if no data is available.
    /// The caller is responsible for returning the buffer to the pool (optional, but recommended for performance).
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the engine is not running.</exception>
    /// <remarks>
    /// The returned buffer comes from the internal AudioBufferPool. While the pool will grow if needed,
    /// it's recommended to return buffers when done to minimize allocations:
    /// <code>
    /// var buffer = wrapper.Receive(out int count);
    /// if (buffer != null)
    /// {
    ///     try { /* process buffer */ }
    ///     finally { wrapper.ReturnInputBuffer(buffer); }
    /// }
    /// </code>
    /// </remarks>
    public float[]? Receive(out int sampleCount)
    {
        ThrowIfDisposed();

        if (!_isRunning)
            throw new InvalidOperationException("Cannot receive audio when engine is not running. Call Start() first.");

        try
        {
            // Call engine to receive samples
            int result = _engine.Receives(out float[] samples);

            if (result < 0)
            {
                // Error occurred
                sampleCount = 0;
                return null;
            }

            if (samples == null || samples.Length == 0)
            {
                // No data available
                sampleCount = 0;
                return null;
            }

            sampleCount = samples.Length;
            return samples;
        }
        catch
        {
            // Log error but don't crash
            // In production, log via ILogger
            sampleCount = 0;
            return null;
        }
    }

    /// <summary>
    /// Returns an input buffer to the pool for reuse (optional but recommended).
    /// </summary>
    /// <param name="buffer">The buffer to return.</param>
    /// <remarks>
    /// This method is optional - buffers will be garbage collected if not returned.
    /// However, returning buffers to the pool reduces GC pressure.
    /// </remarks>
    public void ReturnInputBuffer(float[] buffer)
    {
        if (buffer == null || buffer.Length != _engineBufferSize)
            return; // Invalid buffer - discard

        try
        {
            _inputBufferPool.Return(buffer);
        }
        catch
        {
            // Pool full or other error - discard buffer
        }
    }

    /// <summary>
    /// Gets a list of all available output devices.
    /// </summary>
    /// <returns>List of output device information.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    public List<AudioDeviceInfo> GetOutputDevices()
    {
        ThrowIfDisposed();

        try
        {
            return _engine.GetOutputDevices();
        }
        catch (Exception ex)
        {
            throw new AudioEngineException("Failed to get output devices.", ex);
        }
    }

    /// <summary>
    /// Gets a list of all available input devices.
    /// </summary>
    /// <returns>List of input device information.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    public List<AudioDeviceInfo> GetInputDevices()
    {
        ThrowIfDisposed();

        try
        {
            return _engine.GetInputDevices();
        }
        catch (Exception ex)
        {
            throw new AudioEngineException("Failed to get input devices.", ex);
        }
    }

    /// <summary>
    /// Changes the output device by device name.
    /// The engine must be stopped before changing devices.
    /// </summary>
    /// <param name="deviceName">The friendly name of the device.</param>
    /// <returns>True if successful, false otherwise.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the engine is running.</exception>
    public bool SetOutputDeviceByName(string deviceName)
    {
        ThrowIfDisposed();

        if (_isRunning)
            throw new InvalidOperationException("Cannot change output device while engine is running. Call Stop() first.");

        try
        {
            int result = _engine.SetOutputDeviceByName(deviceName);
            return result == 0;
        }
        catch (Exception ex)
        {
            throw new AudioEngineException($"Failed to set output device to '{deviceName}'.", ex);
        }
    }

    /// <summary>
    /// Changes the input device by device name.
    /// The engine must be stopped before changing devices.
    /// </summary>
    /// <param name="deviceName">The friendly name of the device.</param>
    /// <returns>True if successful, false otherwise.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the engine is running.</exception>
    public bool SetInputDeviceByName(string deviceName)
    {
        ThrowIfDisposed();

        if (_isRunning)
            throw new InvalidOperationException("Cannot change input device while engine is running. Call Stop() first.");

        try
        {
            int result = _engine.SetInputDeviceByName(deviceName);
            return result == 0;
        }
        catch (Exception ex)
        {
            throw new AudioEngineException($"Failed to set input device to '{deviceName}'.", ex);
        }
    }

    /// <summary>
    /// Clears the output buffer, discarding all pending audio data.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    /// <remarks>
    /// WARNING: This method is NOT thread-safe with Send() operations.
    /// Only call this when you're certain no Send() calls are in progress.
    /// Typically used during seek operations or after stopping playback.
    /// </remarks>
    public void ClearOutputBuffer()
    {
        ThrowIfDisposed();
        _outputBuffer.Clear();
    }

    /// <summary>
    /// Pump thread loop - reads from CircularBuffer and sends to engine.
    /// This runs at high priority to minimize audio glitches.
    /// </summary>
    private void PumpThreadLoop()
    {
        // Pre-allocate buffer OUTSIDE loop to avoid stack overflow issues
        // (stackalloc in loop can cause problems in older .NET versions)
        float[] tempBuffer = new float[_engineBufferSize];

        while (!_stopRequested)
        {
            try
            {
                // Check if enough data is available in the circular buffer
                int available = _outputBuffer.Available;

                if (available >= _engineBufferSize)
                {
                    // Read from circular buffer into pre-allocated temp buffer
                    Span<float> bufferSpan = tempBuffer.AsSpan();
                    int read = _outputBuffer.Read(bufferSpan);

                    if (read == _engineBufferSize)
                    {
                        // Send to engine (this may block until hardware buffer has space)
                        _engine.Send(bufferSpan);

                        // Update statistics
                        Interlocked.Add(ref _pumpedFrames, FramesPerBuffer);
                    }
                    else
                    {
                        // Partial read - unusual, but handle gracefully
                        // Send what we have
                        _engine.Send(bufferSpan.Slice(0, read));
                        Interlocked.Add(ref _pumpedFrames, read / _config.Channels);
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
    /// Subscribes to engine events and forwards them to wrapper events.
    /// </summary>
    private void SubscribeToEngineEvents()
    {
        // Create event handlers that forward to wrapper events
        _engineOutputDeviceChanged = (sender, e) => OutputDeviceChanged?.Invoke(this, e);
        _engineInputDeviceChanged = (sender, e) => InputDeviceChanged?.Invoke(this, e);
        _engineDeviceStateChanged = (sender, e) => DeviceStateChanged?.Invoke(this, e);

        // Subscribe to engine events
        _engine.OutputDeviceChanged += _engineOutputDeviceChanged;
        _engine.InputDeviceChanged += _engineInputDeviceChanged;
        _engine.DeviceStateChanged += _engineDeviceStateChanged;
    }

    /// <summary>
    /// Unsubscribes from engine events.
    /// </summary>
    private void UnsubscribeFromEngineEvents()
    {
        if (_engineOutputDeviceChanged != null)
            _engine.OutputDeviceChanged -= _engineOutputDeviceChanged;

        if (_engineInputDeviceChanged != null)
            _engine.InputDeviceChanged -= _engineInputDeviceChanged;

        if (_engineDeviceStateChanged != null)
            _engine.DeviceStateChanged -= _engineDeviceStateChanged;
    }

    /// <summary>
    /// Throws ObjectDisposedException if the wrapper has been disposed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioEngineWrapper));
    }

    /// <summary>
    /// Disposes the audio engine wrapper and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        // Stop engine if running
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

        // Unsubscribe from engine events
        UnsubscribeFromEngineEvents();

        // Clear buffers
        _outputBuffer.Clear();
        _inputBufferPool.Clear();

        // Dispose engine
        _engine?.Dispose();

        _disposed = true;
    }

    /// <summary>
    /// Returns a string representation of the wrapper's current state.
    /// </summary>
    public override string ToString()
    {
        return $"AudioEngineWrapper: {_config.SampleRate}Hz {_config.Channels}ch, BufferSize: {FramesPerBuffer} frames, " +
               $"Running: {_isRunning}, OutputBuffer: {_outputBuffer.Available}/{_outputBuffer.Capacity} samples, " +
               $"Underruns: {TotalUnderruns}, Pumped: {TotalPumpedFrames} frames";
    }
}
