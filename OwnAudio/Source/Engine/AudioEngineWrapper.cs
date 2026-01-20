using System.Runtime.CompilerServices;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.Engine;

/// <summary>
/// Central wrapper class that bridges OwnaudioNET with the external IAudioEngine implementation.
/// Provides simplified interface for audio engine lifecycle, device management, and event forwarding.
/// </summary>
/// <remarks>
/// Architecture:
/// - Main Thread: User API calls (Send, Receive, Start, Stop, device management)
/// - AudioBufferController: Manages circular buffer and buffer pool
/// - AudioPump: Manages pump thread that transfers data to engine
/// - Engine RT Thread: Managed by external IAudioEngine (e.g., WASAPI RT thread)
///
/// Thread Safety:
/// - All public methods are thread-safe
/// - Internal components use lock-free or thread-safe operations
///
/// Performance:
/// - Send() latency: &lt; 1ms (CircularBuffer write only)
/// - Total pipeline latency: ~21ms (2x buffer) + engine buffer (~10ms) = ~31ms @ 512 frames, 48kHz
/// - Zero allocations in Send() hot path
/// </remarks>
public sealed class AudioEngineWrapper : IDisposable
{
    // External engine instance
    private readonly IAudioEngine _engine;

    // Component instances
    private readonly AudioBufferController _bufferController;
    private readonly AudioPump _pump;

    // Configuration
    private readonly AudioConfig _config;

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
    public bool IsRunning => _pump.IsRunning;

    /// <summary>
    /// Gets the number of samples currently available in the output buffer.
    /// </summary>
    public int OutputBufferAvailable => _bufferController.OutputBufferAvailable;

    /// <summary>
    /// Gets the total number of buffer underrun events that have occurred.
    /// </summary>
    public long TotalUnderruns => _bufferController.TotalUnderruns;

    /// <summary>
    /// Gets the total number of frames pumped to the audio engine.
    /// </summary>
    public long TotalPumpedFrames => _pump.TotalPumpedFrames;

    /// <summary>
    /// Gets the underlying IAudioEngine instance.
    /// Useful for advanced scenarios like passing to AudioMixer directly.
    /// </summary>
    public IAudioEngine UnderlyingEngine => _engine;

    /// <summary>
    /// Event raised when the output buffer is full and incoming audio is dropped.
    /// </summary>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun
    {
        add => _bufferController.BufferUnderrun += value;
        remove => _bufferController.BufferUnderrun -= value;
    }

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
        int engineBufferSize = FramesPerBuffer * _config.Channels;

        // Create buffer controller
        _bufferController = new AudioBufferController(
            engineBufferSize,
            _config.Channels,
            bufferMultiplier: 8);

        // Create pump
        _pump = new AudioPump(
            _engine,
            _bufferController,
            engineBufferSize,
            FramesPerBuffer,
            _config.SampleRate);

        // Subscribe to engine events
        SubscribeToEngineEvents();
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

        if (IsRunning)
            return; // Already running

        try
        {
            // Start the external audio engine
            int result = _engine.Start();
            if (result < 0)
                throw new AudioEngineException($"Failed to start audio engine. Error code: {result}", result);

            // Start pump thread
            _pump.Start();
        }
        catch (Exception ex) when (ex is not AudioEngineException)
        {
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

        if (!IsRunning)
            return; // Already stopped

        try
        {
            // Stop pump thread first
            _pump.Stop();

            // Stop the external audio engine
            int result = _engine.Stop();
            if (result < 0)
                throw new AudioEngineException($"Failed to stop audio engine. Error code: {result}", result);
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

        if (!IsRunning)
            throw new InvalidOperationException("Cannot send audio when engine is not running. Call Start() first.");

        _bufferController.Send(samples);
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

        if (!IsRunning)
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

            sampleCount = result; // Use actual samples read, not buffer size
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
        _bufferController.ReturnInputBuffer(buffer);
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

        if (IsRunning)
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

        if (IsRunning)
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
        _bufferController.ClearOutputBuffer();
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
        if (IsRunning)
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

        // Dispose components
        _pump.Dispose();
        _bufferController.Dispose();

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
               $"Running: {IsRunning}, OutputBuffer: {OutputBufferAvailable}/{_bufferController.OutputBufferCapacity} samples, " +
               $"Underruns: {TotalUnderruns}, Pumped: {TotalPumpedFrames} frames";
    }
}
