using System;
using System.Collections.Generic;
using System.Threading;
using Ownaudio.Core;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.Engine;

/// <summary>
/// Fake IAudioEngine for tests and boxes without audio hardware. Fakes the callback timing and counts calls,
/// optionally spits out a 440 Hz sine.
/// </summary>
public sealed class MockAudioEngine : IAudioEngine
{
    private readonly object _lock = new object();
    private readonly bool _generateTestSignal;
    private AudioConfig? _config;
    private Timer? _simulationTimer;
    private volatile int _state;
    private volatile int _sendCallCount;
    private long _totalSamplesSent;
    private volatile int _receiveCallCount;
    private long _totalSamplesReceived;
    private bool _disposed;
    private double _sinePhase;

    /// <summary>
    /// Send() call count.
    /// </summary>
    public int SendCallCount => _sendCallCount;

    /// <summary>
    /// Samples that went through Send().
    /// </summary>
    public long TotalSamplesSent => Interlocked.Read(ref _totalSamplesSent);

    /// <summary>
    /// Receives() call count.
    /// </summary>
    public int ReceiveCallCount => _receiveCallCount;

    /// <summary>
    /// Samples that came back through Receives().
    /// </summary>
    public long TotalSamplesReceived => Interlocked.Read(ref _totalSamplesReceived);

    /// <summary>
    /// Ticks at the fake callback interval, for testing things that hang off audio timing.
    /// </summary>
    public event EventHandler? SimulatedCallback;

    /// <inheritdoc/>
    public event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioDeviceChangedEventArgs>? InputDeviceChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioDeviceStateChangedEventArgs>? DeviceStateChanged;

    /// <inheritdoc/>
#pragma warning disable CS0067
    public event EventHandler<AudioDeviceReconnectedEventArgs>? DeviceReconnected;
#pragma warning restore CS0067

    /// <inheritdoc/>
    public EngineStatus Status
    {
        get
        {
            int s = _state;
            if (s == -1) return EngineStatus.Error;
            if (s == 1)  return EngineStatus.Running;
            return EngineStatus.Idle;
        }
    }

    /// <summary>
    /// Creates the mock. Turn the flag on to get a 440 Hz sine written into the buffers.
    /// </summary>
    /// <param name="generateTestSignal"></param>
    public MockAudioEngine(bool generateTestSignal = false)
    {
        _generateTestSignal = generateTestSignal;
    }

    /// <inheritdoc/>
    public IntPtr GetStream() => IntPtr.Zero;

    /// <inheritdoc/>
    public int FramesPerBuffer
    {
        get
        {
            lock (_lock)
            {
                return _config?.BufferSize ?? 0;
            }
        }
    }

    /// <inheritdoc/>
    public int OwnAudioEngineActivate()
    {
        int _s = _state;
        if (_s < 0) return -1;

        return _s == 1 ? 1 : 0;
    }

    /// <inheritdoc/>
    public int OwnAudioEngineStopped()
    {
        int _s = _state;
        if (_s < 0) return -1;

        return _s == 0 ? 1 : 0;
    }

    /// <inheritdoc/>
    public int Initialize(AudioConfig config)
    {
        if (config == null)
            return -1;

        if (!config.Validate())
            return -2;

        lock (_lock)
        {
            if (_disposed) return -3;
            if (_config != null) return -4;

            _config = config;
            _state = 0;
            _sendCallCount = 0;
            _totalSamplesSent = 0;
            _receiveCallCount = 0;
            _totalSamplesReceived = 0;
            _sinePhase = 0.0;

            return 0;
        }
    }

    /// <inheritdoc/>
    public int Start()
    {
        lock (_lock)
        {
            if (_disposed) return -1;
            if (_config == null) return -2;
            if (_state == 1) return 0;

            double _intervalMs = (_config.BufferSize / (double)_config.SampleRate) * 1000.0;

            _simulationTimer = new Timer(
                _simulationCallback,
                null,
                TimeSpan.FromMilliseconds(_intervalMs),
                TimeSpan.FromMilliseconds(_intervalMs));

            _state = 1;
            return 0;
        }
    }

    /// <inheritdoc/>
    public int Stop()
    {
        lock (_lock)
        {
            if (_disposed) return -1;
            if (_state == 0) return 0;

            _simulationTimer?.Dispose();
            _simulationTimer = null;

            _state = 0;
            return 0;
        }
    }

    /// <inheritdoc/>
    public void Send(Span<float> samples)
    {
        if (_disposed)
            throw new AudioException("Cannot send samples: engine is disposed.");

        if (_config == null)
            throw new AudioException("Cannot send samples: engine not initialized.");

        if (_state != 1)
            throw new AudioException("Cannot send samples: engine not running.");

        Interlocked.Increment(ref _sendCallCount);
        Interlocked.Add(ref _totalSamplesSent, samples.Length);

        if (_generateTestSignal)
            _generateSineWave(samples, _config.SampleRate, _config.Channels);

        Thread.SpinWait(100);
    }

    /// <inheritdoc/>
    public int Receives(Span<float> destination)
    {
        if (_disposed) return -1;
        if (_config == null) return -2;
        if (_state != 1) return -3;
        if (!_config.EnableInput) return -4;

        Interlocked.Increment(ref _receiveCallCount);

        if (_generateTestSignal)
            _generateSineWave(destination, _config.SampleRate, _config.Channels);

        Interlocked.Add(ref _totalSamplesReceived, destination.Length);

        return destination.Length;
    }

    /// <inheritdoc/>
    public List<AudioDeviceInfo> GetOutputDevices()
    {
        return new List<AudioDeviceInfo>
        {
            new AudioDeviceInfo(
                deviceId: "mock-output-default",
                name: "Mock Output Device (Default)",
                engineName: "Mock",
                isInput: false,
                isOutput: true,
                isDefault: true,
                state: AudioDeviceState.Active),
            new AudioDeviceInfo(
                deviceId: "mock-output-secondary",
                name: "Mock Output Device (Secondary)",
                engineName: "Mock",
                isInput: false,
                isOutput: true,
                isDefault: false,
                state: AudioDeviceState.Active)
        };
    }

    /// <inheritdoc/>
    public List<AudioDeviceInfo> GetInputDevices()
    {
        return new List<AudioDeviceInfo>
        {
            new AudioDeviceInfo(
                deviceId: "mock-input-default",
                name: "Mock Input Device (Default)",
                engineName: "Mock",
                isInput: true,
                isOutput: false,
                isDefault: true,
                state: AudioDeviceState.Active),
            new AudioDeviceInfo(
                deviceId: "mock-input-secondary",
                name: "Mock Input Device (Secondary)",
                engineName: "Mock",
                isInput: true,
                isOutput: false,
                isDefault: false,
                state: AudioDeviceState.Active)
        };
    }

    /// <inheritdoc/>
    public int SetOutputDeviceByName(string deviceName)
    {
        if (_disposed) return -1;
        if (_state == 1) return -2;
        if (string.IsNullOrWhiteSpace(deviceName)) return -3;

        return 0;
    }

    /// <inheritdoc/>
    public int SetOutputDeviceByIndex(int deviceIndex)
    {
        if (_disposed) return -1;
        if (_state == 1) return -2;
        if (deviceIndex < 0) return -3;

        return 0;
    }

    /// <inheritdoc/>
    public int SetInputDeviceByName(string deviceName)
    {
        if (_disposed) return -1;
        if (_state == 1) return -2;
        if (string.IsNullOrWhiteSpace(deviceName)) return -3;

        return 0;
    }

    /// <inheritdoc/>
    public int SetInputDeviceByIndex(int deviceIndex)
    {
        if (_disposed) return -1;
        if (_state == 1) return -2;
        if (deviceIndex < 0) return -3;

        return 0;
    }

    /// <inheritdoc/>
    public void PauseDeviceMonitoring()
    {
    }

    /// <inheritdoc/>
    public void ResumeDeviceMonitoring()
    {
    }

    /// <summary>
    /// Zeroes the counters so a test can start from a clean slate.
    /// </summary>
    public void ResetStatistics()
    {
        lock (_lock)
        {
            _sendCallCount = 0;
            _totalSamplesSent = 0;
            _receiveCallCount = 0;
            _totalSamplesReceived = 0;
        }
    }

    /// <summary>
    /// Fires an output device change event on demand.
    /// </summary>
    /// <param name="oldDeviceId"></param>
    /// <param name="newDeviceId"></param>
    public void SimulateOutputDeviceChange(string oldDeviceId, string newDeviceId)
    {
        var _device = new AudioDeviceInfo(
            newDeviceId,
            $"Mock Output Device ({newDeviceId})",
            "Mock",
            isInput: false,
            isOutput: true,
            isDefault: true,
            AudioDeviceState.Active);

        OutputDeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(oldDeviceId, newDeviceId, _device));
    }

    /// <summary>
    /// Fires an input device change event on demand.
    /// </summary>
    /// <param name="oldDeviceId"></param>
    /// <param name="newDeviceId"></param>
    public void SimulateInputDeviceChange(string oldDeviceId, string newDeviceId)
    {
        var _device = new AudioDeviceInfo(
            newDeviceId,
            $"Mock Input Device ({newDeviceId})",
            "Mock",
            isInput: true,
            isOutput: false,
            isDefault: true,
            AudioDeviceState.Active);

        InputDeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(oldDeviceId, newDeviceId, _device));
    }

    /// <summary>
    /// Fires a device state change event on demand.
    /// </summary>
    /// <param name="deviceId"></param>
    /// <param name="newState"></param>
    public void SimulateDeviceStateChange(string deviceId, AudioDeviceState newState)
    {
        var _device = new AudioDeviceInfo(
            deviceId,
            $"Mock Device ({deviceId})",
            "Mock",
            isInput: true,
            isOutput: true,
            isDefault: true,
            newState);

        DeviceStateChanged?.Invoke(this, new AudioDeviceStateChangedEventArgs(deviceId, newState, _device));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            Stop();
            _config = null;
            _state = -1;
            _disposed = true;
        }
    }

    /// <summary>
    /// Timer tick standing in for the audio callback.
    /// </summary>
    /// <param name="state"></param>
    private void _simulationCallback(object? state)
    {
        SimulatedCallback?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fills the buffer with a 440 Hz sine at 25% to stay clear of clipping, phase carries over between calls.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="sampleRate"></param>
    /// <param name="channels"></param>
    private void _generateSineWave(Span<float> buffer, int sampleRate, int channels)
    {
        const double frequency = 440.0;
        const double amplitude = 0.25;
        double _step = 2.0 * Math.PI * frequency / sampleRate;

        int _frames = buffer.Length / channels;

        for (int frame = 0; frame < _frames; frame++)
        {
            float _sample = (float)(amplitude * Math.Sin(_sinePhase));

            for (int ch = 0; ch < channels; ch++)
                buffer[frame * channels + ch] = _sample;

            _sinePhase += _step;

            if (_sinePhase >= 2.0 * Math.PI) _sinePhase -= 2.0 * Math.PI;
        }
    }
}
