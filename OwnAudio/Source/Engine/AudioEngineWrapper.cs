using System.Runtime.CompilerServices;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.Engine;

/// <summary>
/// Bridge between OwnaudioNET and the IAudioEngine implementation: lifecycle, devices, event forwarding.
/// Send() only writes the circular buffer, the pump thread does the blocking work towards the engine.
/// </summary>
public sealed class AudioEngineWrapper : IDisposable
{
    /// <summary>
    /// Keeps concurrent producers apart on the Send path. The buffer is single producer, this makes the
    /// "safe from any thread" promise hold; the pump is the only consumer and never touches this.
    /// </summary>
    private readonly object _sendLock = new();

    private readonly IAudioEngine _engine;
    private readonly AudioBufferController _bufferController;
    private readonly AudioPump _pump;
    private readonly AudioConfig _config;

    private EventHandler<AudioDeviceChangedEventArgs>? _engineOutputDeviceChanged;
    private EventHandler<AudioDeviceChangedEventArgs>? _engineInputDeviceChanged;
    private EventHandler<AudioDeviceStateChangedEventArgs>? _engineDeviceStateChanged;

    private bool _disposed;

    /// <summary>
    /// Frames per buffer the device actually gave us, may differ from what we asked for.
    /// </summary>
    public int FramesPerBuffer { get; }

    /// <summary>
    /// The config we run with.
    /// </summary>
    public AudioConfig Config => _config;

    /// <summary>
    /// True while the pump is going.
    /// </summary>
    public bool IsRunning => _pump.IsRunning;

    /// <summary>
    /// Samples queued for output.
    /// </summary>
    public int OutputBufferAvailable => _bufferController.OutputBufferAvailable;

    /// <summary>
    /// Dropped buffer count so far.
    /// </summary>
    public long TotalUnderruns => _bufferController.TotalUnderruns;

    /// <summary>
    /// Frames handed to the engine so far.
    /// </summary>
    public long TotalPumpedFrames => _pump.TotalPumpedFrames;

    /// <summary>
    /// The raw engine, for cases like passing it straight to AudioMixer.
    /// </summary>
    public IAudioEngine UnderlyingEngine => _engine;

    /// <summary>
    /// Fires when the out buffer is full and audio gets dropped.
    /// </summary>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun
    {
        add => _bufferController.BufferUnderrun += value;
        remove => _bufferController.BufferUnderrun -= value;
    }

    /// <summary>
    /// Output device swapped under us.
    /// </summary>
    public event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged;

    /// <summary>
    /// Input device swapped under us.
    /// </summary>
    public event EventHandler<AudioDeviceChangedEventArgs>? InputDeviceChanged;

    /// <summary>
    /// Device added, removed, enabled or disabled.
    /// </summary>
    public event EventHandler<AudioDeviceStateChangedEventArgs>? DeviceStateChanged;

    /// <summary>
    /// Wraps an already initialized engine. bufferMultiplier is the headroom over one engine buffer,
    /// bump it to 16 or 32 for a mixer with lots of sources or heavy DSP. Default 8 is ~85ms at 48k/512.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="config"></param>
    /// <param name="bufferMultiplier"></param>
    public AudioEngineWrapper(IAudioEngine engine, AudioConfig config, int bufferMultiplier = 8)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        if (bufferMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferMultiplier), "Buffer multiplier must be positive.");

        FramesPerBuffer = _engine.FramesPerBuffer;
        if (FramesPerBuffer <= 0)
            throw new AudioEngineException("Engine FramesPerBuffer must be positive.", -1);

        int _engineBufferSize = FramesPerBuffer * _config.Channels;

        _bufferController = new AudioBufferController(_engineBufferSize, _config.Channels, bufferMultiplier);
        _pump = new AudioPump(_engine, _bufferController, _engineBufferSize, FramesPerBuffer, _config.SampleRate);

        _subscribeEngineEvents();
    }

    /// <summary>
    /// Starts the engine then the pump. Idempotent.
    /// </summary>
    public void Start()
    {
        _throwIfDisposed();

        if (IsRunning) return;

        try
        {
            int _result = _engine.Start();
            if (_result < 0)
                throw new AudioEngineException($"Failed to start audio engine. Error code: {_result}", _result);

            _pump.Start();
        }
        catch (Exception ex) when (ex is not AudioEngineException)
        {
            throw new AudioEngineException("Failed to start audio engine wrapper.", ex);
        }
    }

    /// <summary>
    /// Pump down, buffer flushed, engine stopped. Blocks up to 2s on the pump join, use StopAsync from UI.
    /// </summary>
    public void Stop()
    {
        _throwIfDisposed();

        if (!IsRunning) return;

        try
        {
            _pump.Stop();

            // Flush leftovers, otherwise the next Start() replays stale samples from the old session.
            _bufferController.ClearOutputBuffer();

            int _result = _engine.Stop();
            if (_result < 0)
                throw new AudioEngineException($"Failed to stop audio engine. Error code: {_result}", _result);
        }
        catch (Exception ex) when (ex is not AudioEngineException)
        {
            throw new AudioEngineException("Failed to stop audio engine wrapper.", ex);
        }
    }

    /// <summary>
    /// Stop on a background thread, the one to use from WPF/WinForms/MAUI/Avalonia.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stop();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes interleaved float samples into the circular buffer, no allocation, sub-ms. Any thread is fine.
    /// </summary>
    /// <param name="samples"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(ReadOnlySpan<float> samples)
    {
        _throwIfDisposed();

        if (!IsRunning)
            throw new InvalidOperationException("Cannot send audio when engine is not running. Call Start() first.");

        lock (_sendLock)
        {
            _bufferController.Send(samples);
        }
    }

    /// <summary>
    /// Pulls captured audio. The returned array comes from the pool, hand it back with ReturnInputBuffer.
    /// Null means nothing was available.
    /// </summary>
    /// <param name="sampleCount"></param>
    /// <returns></returns>
    public float[]? Receive(out int sampleCount)
    {
        _throwIfDisposed();

        if (!IsRunning)
            throw new InvalidOperationException("Cannot receive audio when engine is not running. Call Start() first.");

        float[] _buffer = _bufferController.RentInputBuffer()!;
        int _result = _engine.Receives(_buffer.AsSpan());

        if (_result <= 0)
        {
            _bufferController.ReturnInputBuffer(_buffer);
            sampleCount = 0;
            return null;
        }

        sampleCount = _result;
        return _buffer;
    }

    /// <summary>
    /// Gives a capture buffer back to the pool. Optional, but skipping it means GC pressure.
    /// </summary>
    /// <param name="buffer"></param>
    public void ReturnInputBuffer(float[] buffer)
    {
        _bufferController.ReturnInputBuffer(buffer);
    }

    /// <summary>
    /// Every output device we can see.
    /// </summary>
    /// <returns></returns>
    public List<AudioDeviceInfo> GetOutputDevices()
    {
        _throwIfDisposed();

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
    /// Every input device we can see.
    /// </summary>
    /// <returns></returns>
    public List<AudioDeviceInfo> GetInputDevices()
    {
        _throwIfDisposed();

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
    /// Picks an output device by its friendly name. Engine has to be stopped.
    /// </summary>
    /// <param name="deviceName"></param>
    /// <returns></returns>
    public bool SetOutputDeviceByName(string deviceName)
    {
        _throwIfDisposed();

        if (IsRunning)
            throw new InvalidOperationException("Cannot change output device while engine is running. Call Stop() first.");

        try
        {
            return _engine.SetOutputDeviceByName(deviceName) == 0;
        }
        catch (Exception ex)
        {
            throw new AudioEngineException($"Failed to set output device to '{deviceName}'.", ex);
        }
    }

    /// <summary>
    /// Picks an input device by its friendly name. Engine has to be stopped.
    /// </summary>
    /// <param name="deviceName"></param>
    /// <returns></returns>
    public bool SetInputDeviceByName(string deviceName)
    {
        _throwIfDisposed();

        if (IsRunning)
            throw new InvalidOperationException("Cannot change input device while engine is running. Call Stop() first.");

        try
        {
            return _engine.SetInputDeviceByName(deviceName) == 0;
        }
        catch (Exception ex)
        {
            throw new AudioEngineException($"Failed to set input device to '{deviceName}'.", ex);
        }
    }

    /// <summary>
    /// Dumps everything still queued for output. Not safe next to a running Send(), use it around seeks.
    /// </summary>
    public void ClearOutputBuffer()
    {
        _throwIfDisposed();
        _bufferController.ClearOutputBuffer();
    }

    /// <summary>
    /// Parks the device watcher, handy before opening a VST editor window.
    /// </summary>
    public void PauseDeviceMonitoring()
    {
        _throwIfDisposed();
        _engine.PauseDeviceMonitoring();
    }

    /// <summary>
    /// Wakes the device watcher back up.
    /// </summary>
    public void ResumeDeviceMonitoring()
    {
        _throwIfDisposed();
        _engine.ResumeDeviceMonitoring();
    }

    /// <summary>
    /// Hooks the engine events and re-raises them as ours.
    /// </summary>
    private void _subscribeEngineEvents()
    {
        _engineOutputDeviceChanged = (sender, e) => OutputDeviceChanged?.Invoke(this, e);
        _engineInputDeviceChanged = (sender, e) => InputDeviceChanged?.Invoke(this, e);
        _engineDeviceStateChanged = (sender, e) => DeviceStateChanged?.Invoke(this, e);

        _engine.OutputDeviceChanged += _engineOutputDeviceChanged;
        _engine.InputDeviceChanged += _engineInputDeviceChanged;
        _engine.DeviceStateChanged += _engineDeviceStateChanged;
    }

    /// <summary>
    /// Unhooks what we subscribed above.
    /// </summary>
    private void _unsubscribeEngineEvents()
    {
        if (_engineOutputDeviceChanged != null) _engine.OutputDeviceChanged -= _engineOutputDeviceChanged;
        if (_engineInputDeviceChanged != null) _engine.InputDeviceChanged -= _engineInputDeviceChanged;
        if (_engineDeviceStateChanged != null) _engine.DeviceStateChanged -= _engineDeviceStateChanged;
    }

    /// <summary>
    /// Guard for calls after dispose.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _throwIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioEngineWrapper));
    }

    /// <summary>
    /// Stops if needed, unhooks, then tears down pump, buffers and engine.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (IsRunning)
        {
            try { Stop(); }
            catch {}
        }

        _unsubscribeEngineEvents();

        _pump.Dispose();
        _bufferController.Dispose();
        _engine?.Dispose();

        _disposed = true;
    }

    /// <summary>
    /// Short state dump for logs.
    /// </summary>
    public override string ToString()
    {
        return $"AudioEngineWrapper: {_config.SampleRate}Hz {_config.Channels}ch, BufferSize: {FramesPerBuffer} frames, " +
               $"Running: {IsRunning}, OutputBuffer: {OutputBufferAvailable}/{_bufferController.OutputBufferCapacity} samples, " +
               $"Underruns: {TotalUnderruns}, Pumped: {TotalPumpedFrames} frames";
    }
}
