using System.Runtime.CompilerServices;
using Logger;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.Engine;

/// <summary>
/// Bridge between OwnaudioNET and the IAudioEngine implementation: lifecycle, devices, event forwarding.
/// Send() queues straight into the engine's native render ring, so there is no managed buffer and no
/// pump thread on the playback path.
/// </summary>
public sealed class AudioEngineWrapper : IDisposable
{
    /// <summary>
    /// Keeps concurrent producers apart on the Send path. The render ring is single producer, this is
    /// what makes the "safe from any thread" promise hold.
    /// </summary>
    private readonly object _sendLock = new();

    private readonly IAudioEngine _engine;
    private readonly AudioBufferController _bufferController;
    private readonly AudioConfig _config;

    private long _queuedFrames;
    private long _totalUnderruns;
    private volatile bool _running;

    private EventHandler<AudioDeviceChangedEventArgs>? _engineOutputDeviceChanged;
    private EventHandler<AudioDeviceChangedEventArgs>? _engineInputDeviceChanged;
    private EventHandler<AudioDeviceStateChangedEventArgs>? _engineDeviceStateChanged;

    private bool _disposed;

    /// <summary>
    /// The buffer size we run with, in frames. Known from init on, but it's the request —
    /// see OutputCallbackFrames for what the driver granted.
    /// </summary>
    public int FramesPerBuffer { get; }

    /// <summary>
    /// The config we run with.
    /// </summary>
    public AudioConfig Config => _config;

    /// <summary>
    /// True between Start and Stop.
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Samples queued for output, straight off the engine's render ring.
    /// </summary>
    public int OutputBufferAvailable => (_engine as RustAudioEngine)?.OutputQueuedSamples ?? 0;

    /// <summary>
    /// Depth of the engine's render ring in frames, i.e. the playback headroom we're paying for
    /// in latency. Comes from AudioConfig.OutputRingMilliseconds, clamped by the engine.
    /// </summary>
    public int OutputRingFrames => (_engine as RustAudioEngine)?.OutputRingFrames ?? 0;

    /// <summary>
    /// Frames the driver actually hands the render callback, as opposed to FramesPerBuffer, which
    /// is what we asked for. 0 until audio has run; drivers that vary the block size report the
    /// last one. Worth comparing against FramesPerBuffer to catch a silent ASIO rounding.
    /// </summary>
    public int OutputCallbackFrames => (_engine as RustAudioEngine)?.OutputCallbackFrames ?? 0;

    /// <summary>
    /// Same on the capture side. Need not match the render side outside ASIO.
    /// </summary>
    public int InputCallbackFrames => (_engine as RustAudioEngine)?.InputCallbackFrames ?? 0;

    /// <summary>
    /// Channels the playback device really opened with, as opposed to AudioConfig.OutputChannels,
    /// which is only a request — a device that can't serve it gets adapted to the nearest it
    /// supports. Anything drawing physical output sockets, or deciding how far a per-track route
    /// may reach, has to read this. Falls back to the requested width before Start.
    /// </summary>
    public int ActualOutputChannels =>
        (_engine as RustAudioEngine)?.ActualOutputChannels ?? _config.EffectiveOutputChannels;

    /// <summary>
    /// Same on capture, and the range an InputSource.CaptureChannels map may address.
    /// </summary>
    public int ActualInputChannels =>
        (_engine as RustAudioEngine)?.ActualInputChannels ?? _config.EffectiveInputChannels;

    /// <summary>
    /// How many times a Send couldn't queue everything because playback was already buffered up.
    /// </summary>
    public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

    /// <summary>
    /// Frames handed to the engine so far.
    /// </summary>
    public long TotalPumpedFrames => Interlocked.Read(ref _queuedFrames);

    /// <summary>
    /// Capture frames dropped because Receive() wasn't called often enough to keep the input ring
    /// drained. Stays 0 on engines that don't report it.
    /// </summary>
    public long TotalInputOverflowFrames => (_engine as RustAudioEngine)?.InputOverflowFrames ?? 0;

    /// <summary>
    /// The raw engine, for cases like passing it straight to AudioMixer.
    /// </summary>
    public IAudioEngine UnderlyingEngine => _engine;

    /// <summary>
    /// Playback latency in frames, straight off the engine.
    /// </summary>
    public int OutputLatencyFrames => _engine.OutputLatencyFrames;

    /// <summary>
    /// Capture latency in frames, straight off the engine.
    /// </summary>
    public int InputLatencyFrames => _engine.InputLatencyFrames;

    /// <summary>
    /// Fires when playback is buffered up and Send had to drop the tail.
    /// </summary>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

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
    /// Wraps an already initialized engine. bufferMultiplier is kept for source compatibility only —
    /// playback headroom is the engine's native render ring now and no longer tunable from here.
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

        int _engineBufferSize = FramesPerBuffer * _config.EffectiveOutputChannels;

        _bufferController = new AudioBufferController(_engineBufferSize, _config.EffectiveOutputChannels);

        _subscribeEngineEvents();

        Log.Info($"[EngineWrapper] Created: {_config.SampleRate}Hz {_config.Channels}ch, {FramesPerBuffer} frames/buffer");
    }

    /// <summary>
    /// Starts the engine. Idempotent.
    /// </summary>
    public void Start()
    {
        _throwIfDisposed();

        if (IsRunning) return;

        try
        {
            int _result = _engine.Start();
            if (_result < 0)
            {
                Log.Error($"[EngineWrapper] Engine start refused, error code: {_result}");
                throw new AudioEngineException($"Failed to start audio engine. Error code: {_result}", _result);
            }

            _running = true;
            Log.Info("[EngineWrapper] Started");
        }
        catch (Exception ex) when (ex is not AudioEngineException)
        {
            Log.Error("[EngineWrapper] Start failed", ex);
            throw new AudioEngineException("Failed to start audio engine wrapper.", ex);
        }
    }

    /// <summary>
    /// Engine stopped and playback flushed. Can block on the engine, so use StopAsync from a UI thread.
    /// </summary>
    public void Stop()
    {
        _throwIfDisposed();

        if (!IsRunning) return;

        try
        {
            _running = false;

            int _result = _engine.Stop();
            if (_result < 0)
            {
                Log.Error($"[EngineWrapper] Engine stop refused, error code: {_result}");
                throw new AudioEngineException($"Failed to stop audio engine. Error code: {_result}", _result);
            }

            Log.Info($"[EngineWrapper] Stopped after {TotalPumpedFrames} frames, {TotalUnderruns} underruns");
        }
        catch (Exception ex) when (ex is not AudioEngineException)
        {
            Log.Error("[EngineWrapper] Stop failed", ex);
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
    /// Queues interleaved float samples for playback, no allocation, sub-ms. Any thread is fine.
    /// Never blocks: if playback is already buffered up the tail gets dropped and BufferUnderrun fires.
    /// </summary>
    /// <param name="samples"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(ReadOnlySpan<float> samples)
    {
        _throwIfDisposed();

        if (!IsRunning)
            throw new InvalidOperationException("Cannot send audio when engine is not running. Call Start() first.");

        int _queued;
        lock (_sendLock)
        {
            _queued = _engine.TrySend(samples);
        }

        Interlocked.Add(ref _queuedFrames, _queued / _config.EffectiveOutputChannels);

        if (_queued < samples.Length) _raiseUnderrun((samples.Length - _queued) / _config.EffectiveOutputChannels);
    }

    /// <summary>
    /// Bumps the drop counters and tells anyone listening. Off the queueing path on purpose,
    /// a slow handler shouldn't sit in the middle of Send.
    /// </summary>
    /// <param name="missedFrames"></param>
    private void _raiseUnderrun(int missedFrames)
    {
        Interlocked.Increment(ref _totalUnderruns);

        BufferUnderrun?.Invoke(this, new BufferUnderrunEventArgs(
            missedFrames: missedFrames,
            position: Interlocked.Read(ref _queuedFrames)));
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
            Log.Error("[EngineWrapper] Output device enumeration failed", ex);
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
            Log.Error("[EngineWrapper] Input device enumeration failed", ex);
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
            bool _ok = _engine.SetOutputDeviceByName(deviceName) == 0;
            if (_ok) { Log.Info($"[EngineWrapper] Output device set to '{deviceName}'"); }
            else { Log.Warning($"[EngineWrapper] Engine rejected output device '{deviceName}'"); }
            return _ok;
        }
        catch (NotSupportedException ex)
        {
            // The engine explains why the host can't do this; wrapping would bury that message.
            Log.Warning($"[EngineWrapper] Host cannot switch output device: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[EngineWrapper] Setting output device to '{deviceName}' failed", ex);
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
            bool _ok = _engine.SetInputDeviceByName(deviceName) == 0;
            if (_ok) { Log.Info($"[EngineWrapper] Input device set to '{deviceName}'"); }
            else { Log.Warning($"[EngineWrapper] Engine rejected input device '{deviceName}'"); }
            return _ok;
        }
        catch (NotSupportedException ex)
        {
            // The engine explains why the host can't do this; wrapping would bury that message.
            Log.Warning($"[EngineWrapper] Host cannot switch input device: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[EngineWrapper] Setting input device to '{deviceName}' failed", ex);
            throw new AudioEngineException($"Failed to set input device to '{deviceName}'.", ex);
        }
    }

    /// <summary>
    /// Dumps everything still queued for output. Not safe next to a running Send(), use it around seeks.
    /// </summary>
    public void ClearOutputBuffer()
    {
        _throwIfDisposed();
        (_engine as RustAudioEngine)?.ClearOutput();
    }

    /// <summary>
    /// Parks the device watcher, handy before opening a VST editor window.
    /// </summary>
    public void PauseDeviceMonitoring()
    {
        _throwIfDisposed();
        _engine.PauseDeviceMonitoring();
        Log.Info("[EngineWrapper] Device monitoring paused");
    }

    /// <summary>
    /// Wakes the device watcher back up.
    /// </summary>
    public void ResumeDeviceMonitoring()
    {
        _throwIfDisposed();
        _engine.ResumeDeviceMonitoring();
        Log.Info("[EngineWrapper] Device monitoring resumed");
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
            catch (Exception ex) { Log.Warning($"[EngineWrapper] Stop during dispose failed, tearing down anyway: {ex.Message}"); }
        }

        _unsubscribeEngineEvents();

        _bufferController.Dispose();
        _engine?.Dispose();

        _disposed = true;
        Log.Info("[EngineWrapper] Disposed");
    }

    /// <summary>
    /// Short state dump for logs.
    /// </summary>
    public override string ToString()
    {
        return $"AudioEngineWrapper: {_config.SampleRate}Hz {_config.Channels}ch, BufferSize: {FramesPerBuffer} frames, " +
               $"Running: {IsRunning}, Queued: {OutputBufferAvailable} samples, " +
               $"Underruns: {TotalUnderruns}, Pumped: {TotalPumpedFrames} frames";
    }
}
